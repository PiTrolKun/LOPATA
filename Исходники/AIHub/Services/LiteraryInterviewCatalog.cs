namespace AIHub.Services;

public enum InterviewInput { Text, Folder, Name, Memory, Form, Genres, Basis, Materials, Route, Author, Title }
public sealed record InterviewQuestion(int Number, int Topic, InterviewInput Input = InterviewInput.Text,
    string[]? Options = null)
{
    public string Key => "Literary.Interview.Q" + Number;
    public bool Creative => Input == InterviewInput.Text;
}

public static class LiteraryInterviewCatalog
{
    // Temporary user-requested shortcuts; remove after the manual acceptance round.
    public static bool TestNavigationEnabled { get; set; } = false;
    public static readonly int[] AdaptiveEnds = [8, 14, 20, 22, 25, 30, 34];
    public static readonly InterviewQuestion[] Questions =
    [
        new(1,0,InterviewInput.Folder), new(2,0,InterviewInput.Name), new(3,0,InterviewInput.Memory),
        new(4,1), new(5,1,InterviewInput.Form), new(6,1,InterviewInput.Genres),
        new(7,1,Options:["Adults","Teens","Children"]), new(8,1,Options:["Hope","Tension","Wonder","Fun"]),
        new(9,2,InterviewInput.Basis), new(10,2,InterviewInput.Materials), new(11,2,Options:["NoSource"]),
        new(12,2), new(13,2,Options:["Present","Past","Future"]), new(14,2),
        new(15,3), new(16,3), new(17,3), new(18,3), new(19,3), new(20,3),
        new(21,4,Options:["OrdinaryWorld"]), new(22,4,Options:["NoSpecialLimits"]),
        new(23,5), new(24,5), new(25,5,Options:["Happy","Open","Tragic"]),
        new(26,6,Options:["FirstPerson","ThirdPerson"]), new(27,6,Options:["Calm","Dark","Humorous"]),
        new(28,6,Options:["Simple","Poetic"]), new(29,6,Options:["Slow","Fast","Variable"]),
        new(30,6,Options:["Brief","Detailed"]), new(31,7), new(32,7,Options:["NoSpecialLimits"]),
        new(33,7), new(34,7), new(35,8,InterviewInput.Route), new(36,9,InterviewInput.Author),
        new(37,9,InterviewInput.Title)
    ];
    public static readonly string[] Genres = ["fantasy","scifi","mystery","detective","adventure","romance",
        "drama","comedy","historical","horror","thriller","fairytale","sliceoflife","wuxia","xianxia",
        "litrpg","progression","portal","cyberpunk","steampunk","postapocalypse","dystopia","utopia",
        "magicalrealism","satire","absurd","psychological","epic","documentary","essay","poetry"];
    public static InterviewQuestion Get(int number) => Questions[Math.Clamp(number, 1, 37) - 1];
}
