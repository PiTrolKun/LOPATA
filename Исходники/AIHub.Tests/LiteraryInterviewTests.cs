using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryInterviewTests
{
    private string _parent = "";
    [TestInitialize] public void Setup() { LiteraryInterviewCatalog.TestNavigationEnabled=true; _parent=Path.Combine(Path.GetTempPath(),"LopataInterview-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_parent); }
    [TestCleanup] public void Cleanup() { LiteraryInterviewCatalog.TestNavigationEnabled=false; if (Directory.Exists(_parent)) Directory.Delete(_parent,true); }
    private LiteraryInterviewSession Session()
    {
        var s = new LiteraryInterviewSession("ru",_parent); s.State.ProjectName="Тест"; s.Reserve(); return s;
    }
    [TestMethod] public void PendingCorrectionAndDisabledAiSurviveRestartWithoutBecomingFacts()
    {
        string root;
        using (var s=Session())
        {
            root=s.Root!; s.State.Step=4; s.State.Inputs[4]="Может быть врач";
            s.Propose("Врач",false); s.State.Correction="Пока не решил"; s.State.Stage=InterviewStage.Correction;
            s.State.AiDisabled=true; s.State.InFlight=true; s.Save();
        }
        using var resumed=LiteraryInterviewSession.Resume(root);
        Assert.AreEqual(0,resumed.State.Records.Count); Assert.IsFalse(resumed.State.InFlight);
        Assert.IsTrue(resumed.State.AiDisabled); Assert.AreEqual("Пока не решил",resumed.State.Correction);
        Assert.AreEqual(InterviewStage.Correction,resumed.State.Stage);
        resumed.Propose("Врач — возможная профессия, ещё не решено.",false);
        resumed.Confirm("Кто герой?","Может быть врач"); resumed.Save();
        Assert.AreEqual("Врач — возможная профессия, ещё не решено.",resumed.State.Records.Single().Text);
        Assert.AreEqual(5,resumed.State.Step);
    }
    [TestMethod] public void BackupRecoversLastSuccessfulCheckpoint()
    {
        string root;
        using(var s=Session()) { root=s.Root!; s.State.Inputs[4]="Первый вариант"; s.Save(); s.State.Inputs[4]="Второй вариант"; s.Save(); }
        File.WriteAllText(Path.Combine(root,LiteraryInterviewSession.RelativeFile),"{broken");
        using var resumed=LiteraryInterviewSession.Resume(root);
        Assert.AreEqual("Первый вариант",resumed.State.Inputs[4]);
    }
    [TestMethod] public void TestNavigationDoesNotConfirmOrDiscardTypedAnswers()
    {
        using var s=Session(); s.State.Step=4; s.State.Inputs[4]="Сырой замысел";
        s.TestMove(1); s.TestMove(-1);
        Assert.AreEqual(4,s.State.Step); Assert.AreEqual("Сырой замысел",s.State.Inputs[4]); Assert.AreEqual(0,s.State.Records.Count);
        Assert.IsFalse(s.Complete);
    }
    [TestMethod] public void AdaptiveQuestionsRequireWholeTopicAndStopOnlyByUser()
    {
        using var s=Session();
        for (var i=4;i<=8;i++) { s.State.Step=i; s.Propose("Ответ "+i,false); s.Confirm("Вопрос "+i,"Ответ "+i); }
        Assert.AreEqual(InterviewStage.Adaptive,s.State.Stage); Assert.IsTrue(s.TopicMinimumComplete());
        s.State.AdaptiveQuestion="Почему?"; s.Propose("Потому",true); s.Confirm("Почему?","Потому");
        Assert.AreEqual(8,s.State.Step); Assert.AreEqual(InterviewStage.Adaptive,s.State.Stage);
        s.EndTopic(); Assert.AreEqual(9,s.State.Step);
    }
    [TestMethod] public void UnderstandingRequestExcludesQuestionInstructionAndUnapprovedInterpretation()
    {
        using var s=Session(); s.State.Step=4; s.State.Inputs[4]="Сырой ответ"; s.State.Pending="Неверно";
        var messages=LiteraryInterviewPrompts.Build(s.State,false,"Замысел?","Замысел","Алексей");
        Assert.AreEqual("Скажи как понял.",messages[0].Content);
        Assert.IsFalse(messages[1].Content.Contains("Неверно"));
        var packet=LiteraryInterviewPrompts.Packet(s.State); Assert.IsFalse(packet.Contains("Алексей")); Assert.IsFalse(packet.Contains("Сырой ответ"));
        Assert.IsFalse(messages[1].Content.Contains("Алексей"));
    }
    [TestMethod] public void UnderstandingCannotAccumulatePreviousSummaries()
    {
        using var s=Session(); s.State.Step=12; s.State.Inputs[12]="Кемеровская область";
        s.State.Records.Add(new("old",4,1,"Идея?","Ответ","Старый пересказ " + new string('x',20000),"intent",false));
        var messages=LiteraryInterviewPrompts.Build(s.State,false,"Где?","Мир","Петр");
        Assert.IsFalse(messages[1].Content.Contains("Старый пересказ"));
        Assert.IsTrue(messages[1].Content.Length<400);
        s.State.Pending="Москва"; s.State.Correction="Нет, Кемеровская область";
        messages=LiteraryInterviewPrompts.Build(s.State,false,"Где?","Мир","");
        Assert.IsTrue(messages[1].Content.Contains("Москва"));
        Assert.IsFalse(messages[1].Content.Contains("Старый пересказ"));
    }
    [TestMethod] public async Task QuestionReadsOnlySelectedConfirmedTopicFromDisk()
    {
        using var s=Session();
        s.State.Records.Add(new("a",4,1,"Идея?","raw","Почтальон","intent",false));
        s.State.Records.Add(new("b",12,2,"Где?","raw","Луна","setting",false));
        s.State.Pending="Не утверждено"; s.State.Inputs[15]="Черновик"; s.Save();
        s.State.Records.Add(new("unsaved",12,2,"Где?","raw","Не записано на диск","setting",false));
        var call=0; var traced=false;
        var result=await LiteraryInterviewQuestions.AskAsync(s.State,s.Root!,"Мир","Петр",(messages,schema)=>
        {
            call++;
            if(call==1)
            {
                Assert.IsFalse(messages[1].Content.Contains("Почтальон"));
                Assert.IsFalse(messages[1].Content.Contains("Луна"));
                return Task.FromResult("{\"topics\":[2]}");
            }
            Assert.IsTrue(messages[1].Content.Contains("Луна"));
            foreach(var excluded in new[]{"Почтальон","Не утверждено","Черновик","Не записано на диск"})
                Assert.IsFalse(messages[1].Content.Contains(excluded));
            return Task.FromResult("{\"question\":\"Какая погода на Луне?\"}");
        },(_,_)=>traced=true);
        Assert.AreEqual(2,call); Assert.IsTrue(traced); Assert.AreEqual("Какая погода на Луне?",result);
        Assert.Throws<InvalidDataException>(()=>LiteraryInterviewQuestions.ReadTopics(s.Root!,[0]));
        Assert.Throws<InvalidDataException>(()=>LiteraryInterviewQuestions.ReadTopics(s.Root!,[99]));
    }
    [TestMethod] public void QuestionValidationRejectsMultipleAndRepeatedQuestions()
    {
        var asked=new[]{new InterviewAsked(1,"Что движет героем?")};
        Assert.IsFalse(LiteraryInterviewQuestions.ValidQuestion("Что движет героем?",asked));
        Assert.IsFalse(LiteraryInterviewQuestions.ValidQuestion("Кто он? Где живёт?",asked));
        Assert.IsTrue(LiteraryInterviewQuestions.ValidQuestion("Чего боится герой?",asked));
    }
    [TestMethod] public void RecoveredAnswerRequiresConfirmationThenReturnsToSavedStep()
    {
        string root;
        using(var s=Session())
        {
            root=s.Root!; s.State.Step=8; s.State.AdaptiveInput="Энергетики";
            s.State.Inputs[12]="Город"; s.State.ConfirmationReturnStep=12;
            s.Propose("Энергетики",true); s.Save();
        }
        using var resumed=LiteraryInterviewSession.Resume(root);
        Assert.AreEqual(0,resumed.State.Records.Count);
        resumed.TestMove(1); Assert.IsNull(resumed.State.ConfirmationReturnStep);
        resumed.TestMove(-1); Assert.AreEqual(12,resumed.State.ConfirmationReturnStep);
        resumed.Confirm("Привычки?","Энергетики"); resumed.Save();
        Assert.AreEqual(12,resumed.State.Step); Assert.AreEqual("Город",resumed.State.Inputs[12]);
        Assert.AreEqual(1,resumed.State.Records.Count); Assert.IsNull(resumed.State.ConfirmationReturnStep);
    }
    [TestMethod] public void FullBriefReachesBothRolesWithoutInterviewJournal()
    {
        using var s=Session(); s.State.Step=25; s.Propose("В финале герой уедет.",false); s.Confirm("Финал?","Уедет","plan");
        s.Journal("unapproved","Это отвергнутый текст"); s.State.Route[0].Title="Начало"; s.State.Route[0].Description="Пока не финал";
        var project=LiteraryInterviewPrompts.Project(s.State,"Без названия");
        foreach (var role in Enum.GetValues<LiteraryChatProfile>())
        {
            var messages=LiteraryModelPolicy.Messages(role,[],"",project);
            Assert.IsTrue(messages[0].Content.Contains("В финале герой уедет."));
            Assert.IsFalse(messages[0].Content.Contains("Это отвергнутый текст"));
        }
        Assert.IsTrue(project.CreationBrief.Contains("not_manuscript_events"));
    }
    [TestMethod] public void EmptyAuthorIsAllowedButEmptyModelUnderstandingIsNot()
    {
        using var s=Session(); s.State.Step=36;
        Assert.Throws<InvalidDataException>(()=>s.Propose("",false));
        s.Propose("",false,allowEmpty:true); s.Confirm("Автор?","");
        Assert.AreEqual("",s.State.Records.Single().Text); Assert.AreEqual(37,s.State.Step);
    }
    [TestMethod] public void WrongSchemaAndConcurrentResumeCannotOverwriteDraft()
    {
        using var s=Session();
        Assert.Throws<IOException>(()=>LiteraryInterviewSession.Resume(s.Root!));
        Assert.Throws<IOException>(()=>new LiteraryInterviewSession("ru",_parent) { State = { ProjectName="Тест" } }.Reserve());
    }
    [TestMethod] public void QuestionsAndManualRouteHaveAgreedShape()
    {
        CollectionAssert.AreEqual(Enumerable.Range(1,37).ToArray(),LiteraryInterviewCatalog.Questions.Select(q=>q.Number).ToArray());
        CollectionAssert.AreEqual(new[]{8,14,20,22,25,30,34},LiteraryInterviewCatalog.AdaptiveEnds);
        Assert.AreEqual(InterviewInput.Route,LiteraryInterviewCatalog.Get(35).Input);
        Assert.IsFalse(LiteraryInterviewCatalog.Get(35).Creative);
        Assert.IsTrue(LiteraryInterviewCatalog.Genres.Contains("wuxia"));
    }
    [TestMethod] public void ContextExhaustionDuringAdaptiveUnderstandingContinuesMandatoryQuestions()
    {
        using var s=Session();
        for(var step=4;step<=8;step++) { s.State.Step=step; s.Propose("Ответ",false); s.Confirm("Вопрос","Ответ"); }
        s.State.AiDisabled=true; s.Propose("Мой дословный ответ",true); s.Confirm("Уточнение?","Мой дословный ответ");
        Assert.AreEqual(9,s.State.Step); Assert.AreEqual(InterviewStage.Mandatory,s.State.Stage);
        s.Save(); Assert.IsTrue(LiteraryInterviewSession.Read(s.Root!).AiDisabled);
    }
    [TestMethod] public void TestNavigationRestoresUnconfirmedCorrectionWithoutApprovingIt()
    {
        using var s=Session(); s.State.Step=4; s.Propose("Он врач",false);
        s.State.Stage=InterviewStage.Correction; s.State.Correction="Ещё не решил";
        s.TestMove(1); s.TestMove(-1);
        Assert.AreEqual(InterviewStage.Correction,s.State.Stage); Assert.AreEqual("Ещё не решил",s.State.Correction);
        Assert.AreEqual("Он врач",s.State.Pending); Assert.AreEqual(0,s.State.Records.Count);
    }
}
