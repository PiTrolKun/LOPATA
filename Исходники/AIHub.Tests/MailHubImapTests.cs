using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AIHub.Services.MailHub;
using Lopata.MailHub;
using MailKit.Net.Imap;

namespace AIHub.Tests;

[TestClass]
public sealed class MailHubImapTests
{
    [TestMethod]
    public async Task SelectedFolderIsReadOnlyAndOnlyTheTextPartIsFetched()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var key = RSA.Create(2048);
        using var generated = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx, ""), "",
            X509KeyStorageFlags.UserKeySet);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var directory = Directory.CreateTempSubdirectory("lopata-mail-imap-test-");
        try
        {
            var files = new MailHubFiles(directory.FullName);
            var configuration = new MailHubConfiguration(files.Root);
            var account = MailHubAccount.Create(MailHubProvider.Other, "reader@example.test", "reader",
                "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, false, MailHubAuthentication.AppPassword)
                with { Folders = ["INBOX"] };
            configuration.WriteSecret(account.Id, new(AppPassword: "fixture"));
            var imap = new MailHubImap(configuration, () => new ImapClient
            {
                ServerCertificateValidationCallback = (_, remote, _, _) => remote?.GetCertHashString() == certificate.Thumbprint
            });
            var messages = new List<MailHubMessage>();
            Task Accept(IReadOnlyList<MailHubMessage> batch, MailHubReadResult _, CancellationToken cancellation)
            { messages.AddRange(batch); return Task.CompletedTask; }

            var firstServer = ServeAsync(listener, certificate, timeout.Token);
            MailHubReadResult first;
            try { first = await imap.ReadAsync(account, new MailHubState(), true, Accept, timeout.Token); }
            catch
            {
                if (firstServer.IsFaulted) await firstServer;
                throw;
            }
            var commands = await firstServer;
            Assert.AreEqual("Verification code [code]", messages.Single().Subject);
            Assert.AreEqual("Your verification code is [code].", messages.Single().Text.Trim());
            Assert.IsTrue(messages.Single().HasAttachment);
            Assert.AreEqual(new MailHubCursor(123, 7), first.Cursors.Values.Single());
            Assert.IsTrue(commands.Any(command => command.StartsWith("EXAMINE INBOX", StringComparison.Ordinal)));
            Assert.IsTrue(commands.Any(command => command.Contains("BODY.PEEK[1]", StringComparison.Ordinal)));
            Assert.IsFalse(commands.Any(command => command.Contains("STORE", StringComparison.Ordinal)
                || command.StartsWith("SELECT ", StringComparison.Ordinal) || command.Contains("BODY.PEEK[2", StringComparison.Ordinal)));

            messages.Clear();
            var secondServer = ServeAsync(listener, certificate, timeout.Token);
            await imap.ReadAsync(account, new MailHubState(), true, Accept, timeout.Token, resume: first);
            Assert.HasCount(0, messages);
            Assert.IsFalse((await secondServer).Any(command => command.StartsWith("UID FETCH", StringComparison.Ordinal)));

            var incompatible = ServeAsync(listener, certificate, timeout.Token, validity: 124);
            await Assert.ThrowsExactlyAsync<MailHubCheckpointChangedException>(() =>
                imap.ReadAsync(account, new MailHubState(), true, Accept, timeout.Token, resume: first));
            Assert.IsFalse((await incompatible).Any(command => command.StartsWith("UID FETCH", StringComparison.Ordinal)));

            var changedServer = ServeAsync(listener, certificate, timeout.Token, validity: 124);
            var reset = await imap.ReadAsync(account, new MailHubState { Cursors = first.Cursors }, false, Accept, timeout.Token);
            _ = await changedServer;
            Assert.AreEqual(new MailHubCursor(124, 7), reset.Cursors.Values.Single());
            Assert.IsFalse(messages.Single().HasAttachment);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task<List<string>> ServeAsync(TcpListener listener, X509Certificate2 certificate, CancellationToken token,
        uint validity = 123)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        await using var stream = new SslStream(client.GetStream());
        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token);
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("* OK [CAPABILITY IMAP4rev1] test server");
        var commands = new List<string>();
        while (await reader.ReadLineAsync(token) is { } line)
        {
            int space = line.IndexOf(' ');
            string tag = line[..space];
            string command = line[(space + 1)..];
            commands.Add(command);
            string verb = command.Split(' ', 2)[0];
            string response = verb switch
            {
                "CAPABILITY" => "* CAPABILITY IMAP4rev1\r\n",
                "LOGIN" => "",
                "LIST" => "* LIST () \"/\" INBOX\r\n",
                "EXAMINE" => $"* 1 EXISTS\r\n* 0 RECENT\r\n* OK [UIDVALIDITY {validity}] test\r\n* OK [UIDNEXT 8] test\r\n",
                "UID" when command.StartsWith("UID SEARCH", StringComparison.Ordinal) => "* SEARCH 7\r\n",
                "UID" when command.Contains("BODYSTRUCTURE", StringComparison.Ordinal) =>
                    "* 1 FETCH (UID 7 ENVELOPE (\"Thu, 19 Nov 2026 12:00:00 +0700\" \"Verification code 123456\" ((\"Sender\" NIL \"sender\" \"example.test\")) NIL NIL NIL NIL NIL NIL \"<fixture@example.test>\") BODYSTRUCTURE ((\"TEXT\" \"PLAIN\" (\"CHARSET\" \"UTF-8\") NIL NIL \"7BIT\" 33 1 NIL NIL NIL NIL) (\"APPLICATION\" \"OCTET-STREAM\" (\"NAME\" \"private.bin\") NIL NIL \"BASE64\" 100 NIL (\"ATTACHMENT\" (\"FILENAME\" \"private.bin\")) NIL NIL) \"MIXED\" (\"BOUNDARY\" \"test\") NIL NIL NIL))\r\n",
                "UID" when command.Contains("BODY.PEEK[1]", StringComparison.Ordinal) =>
                    BodyPart(command),
                _ => ""
            };
            bool supported = response.Length > 0 || verb is "LOGIN" or "LOGOUT";
            var status = verb == "EXAMINE" ? "OK [READ-ONLY]" : supported ? "OK" : "BAD";
            await writer.WriteAsync((response + tag + " " + status + " test\r\n").AsMemory(), token);
            if (verb == "LOGOUT") break;
        }
        return commands;
    }

    private static string BodyPart(string command)
    {
        const string mime = "Content-Type: text/plain; charset=utf-8\r\n\r\n";
        const string body = "Your verification code is 123456.";
        string header = command.Contains("BODY.PEEK[1.MIME]", StringComparison.Ordinal) ? $" BODY[1.MIME] {{{mime.Length}}}\r\n{mime}" : "";
        return $"* 1 FETCH (UID 7{header} BODY[1] {{{body.Length}}}\r\n{body})\r\n";
    }
}
