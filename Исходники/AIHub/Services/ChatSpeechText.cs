using System.Text.RegularExpressions;

namespace AIHub.Services;

internal static class ChatSpeechText
{
    // Speech content follows the selected synthesis language, independently of the UI language.
    private static readonly IReadOnlyDictionary<string, string> Tests = new Dictionary<string, string>
    {
        ["ru"] = "Привет! Это проверка голоса в чате. Можно спокойно поговорить о том, что сейчас важно.",
        ["en"] = "Hello! This is a chat voice test. We can take our time and talk about what matters to you.",
        ["zh"] = "你好！这是聊天语音测试。我们可以慢慢聊聊你现在关心的事情。",
        ["yue"] = "你好！呢個係語音測試。我哋可以慢慢傾。",
        ["ja"] = "こんにちは。これはチャットの音声テストです。ゆっくり話しましょう。",
        ["ko"] = "안녕하세요. 채팅 음성 테스트입니다. 천천히 이야기해요.",
        ["de"] = "Hallo! Dies ist ein Sprachtest für den Chat. Wir können in Ruhe miteinander sprechen.",
        ["fr"] = "Bonjour ! Ceci est un test de voix du chat. Nous pouvons parler tranquillement.",
        ["it"] = "Ciao! Questa è una prova della voce della chat. Possiamo parlare con calma.",
        ["es"] = "Hola. Esta es una prueba de voz del chat. Podemos hablar con calma.",
        ["pt"] = "Olá! Este é um teste de voz do chat. Podemos conversar com calma.",
        ["ar"] = "مرحباً! هذا اختبار للصوت في المحادثة. يمكننا التحدث بهدوء.",
        ["bg"] = "Здравей! Това е проверка на гласа в чата. Можем да поговорим спокойно.",
        ["cs"] = "Ahoj! Toto je zkouška hlasu v chatu. Můžeme si v klidu promluvit.",
        ["da"] = "Hej! Dette er en stemmetest i chatten. Vi kan tale roligt sammen.",
        ["el"] = "Γεια σου! Αυτή είναι μια δοκιμή φωνής. Μπορούμε να μιλήσουμε ήρεμα.",
        ["et"] = "Tere! See on vestluse hääleproov. Võime rahulikult rääkida.",
        ["fi"] = "Hei! Tämä on keskustelun äänitesti. Voimme jutella rauhassa.",
        ["hi"] = "नमस्ते! यह चैट की आवाज़ का परीक्षण है। हम आराम से बात कर सकते हैं।",
        ["hr"] = "Bok! Ovo je provjera glasa u razgovoru. Možemo mirno razgovarati.",
        ["hu"] = "Szia! Ez egy hangpróba. Nyugodtan beszélgethetünk.",
        ["id"] = "Halo! Ini adalah tes suara percakapan. Kita bisa berbicara dengan tenang.",
        ["lt"] = "Labas! Tai pokalbio balso bandymas. Galime ramiai pasikalbėti.",
        ["lv"] = "Sveiki! Šis ir sarunas balss tests. Varam mierīgi parunāt.",
        ["nl"] = "Hallo! Dit is een stemtest voor de chat. We kunnen rustig praten.",
        ["pl"] = "Cześć! To jest test głosu w czacie. Możemy spokojnie porozmawiać.",
        ["ro"] = "Salut! Acesta este un test al vocii din chat. Putem vorbi în liniște.",
        ["sk"] = "Ahoj! Toto je skúška hlasu v čete. Môžeme sa pokojne porozprávať.",
        ["sl"] = "Živjo! To je preizkus glasu v klepetu. Lahko se mirno pogovoriva.",
        ["sv"] = "Hej! Det här är ett rösttest för chatten. Vi kan prata i lugn och ro.",
        ["tr"] = "Merhaba! Bu bir sohbet sesi testidir. Sakin bir şekilde konuşabiliriz.",
        ["uk"] = "Привіт! Це перевірка голосу в чаті. Можемо спокійно поговорити про важливе.",
        ["vi"] = "Xin chào! Đây là bài kiểm tra giọng nói. Chúng ta có thể trò chuyện bình tĩnh."
    };
    internal static string TestPhrase(string language) => Tests.TryGetValue(language, out var text) ? text : throw new InvalidOperationException("ChatSpeech.Invalid");
    internal static string Plain(string text)
    {
        text = Regex.Replace(text, @"!\[([^\]]*)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"(?m)^\s{0,3}(?:#{1,6}\s+|>\s?|[-*+]\s+)", "");
        text = Regex.Replace(text, @"(?m)^\s*```[^\n]*$", "");
        text = text.Replace("**", "").Replace("__", "").Replace("`", "");
        return text.Trim();
    }

    internal static IReadOnlyList<string> Parts(string text, int maximum = 600)
    {
        text = Plain(text); var parts = new List<string>();
        while (text.Length > maximum)
        {
            var split = text.LastIndexOfAny(['.', '!', '?', '\n', ';'], maximum - 1, maximum / 2);
            if (split >= maximum / 2) split++;
            else { split = text.LastIndexOf(' ', maximum - 1, maximum / 2); if (split < maximum / 2) split = maximum; }
            if (char.IsHighSurrogate(text[split - 1])) split--;
            parts.Add(text[..split].Trim()); text = text[split..].TrimStart();
        }
        if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
        return parts;
    }
}
