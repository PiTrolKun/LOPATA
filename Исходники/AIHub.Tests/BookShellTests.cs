using AIHub.Services.Books;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace AIHub.Tests;

[TestClass]
public sealed class BookShellTests
{
    [TestMethod]
    public void SingleFileParserRejectsBatchAndCommandInjection()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => BookShellRequest.Parse(["--shell-book", "epub", "--", "C:\\one.txt", "C:\\two.txt"]));
        Assert.ThrowsExactly<InvalidDataException>(() => BookShellRequest.Parse(["--shell-book", "exe", "--", "C:\\one.txt"]));
        Assert.ThrowsExactly<InvalidDataException>(() => BookShellRequest.Parse(["--shell-book", "pdf", "--", "one.txt"]));
        Assert.ThrowsExactly<InvalidDataException>(() => BookShellRequest.Parse(["--shell-book", "txt", "--", "C:\\x\n.txt"]));
    }

    [TestMethod]
    public void RegistryVerbsAreSingleAndDoNotChangeOtherOwners()
    {
        var parent = @"Software\AIHub.Tests\" + Guid.NewGuid().ToString("N");
        var path = parent + @"\BookShellIntegration";
        try
        {
            var integration = new BookShellIntegration(typeof(BookShellTests).Assembly.Location, parent);
            integration.Apply(true, key => key == "BookConverter.ToFormat" ? "To {0}" : "Text to book");
            Assert.IsTrue(integration.IsEnabled);
            using (var root = Registry.CurrentUser.OpenSubKey(path)!)
            {
                Assert.AreEqual("Single", root.GetValue("MultiSelectModel"));
                using var verb = root.OpenSubKey(@"shell\epub")!; Assert.AreEqual("Single", verb.GetValue("MultiSelectModel"));
                using var command = verb.OpenSubKey("command")!;
                StringAssert.Contains((string)command.GetValue("")!, "--shell-book epub -- \"%1\"");
            }
            integration.Apply(false, _ => ""); Assert.IsFalse(integration.IsEnabled);
            using (var foreign = Registry.CurrentUser.CreateSubKey(path)) { foreign.SetValue("LOPATA.Owner", "other"); foreign.SetValue("keep", "foreign-data"); }
            Assert.ThrowsExactly<InvalidOperationException>(() => integration.Apply(true, _ => ""));
            integration.Apply(false, _ => "");
            using var remaining = Registry.CurrentUser.OpenSubKey(path)!; Assert.AreEqual("foreign-data", remaining.GetValue("keep"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(parent, false); }
    }
}
