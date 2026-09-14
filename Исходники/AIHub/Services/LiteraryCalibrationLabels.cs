namespace AIHub.Services;

public sealed class LiteraryCalibrationLabels
{
    private readonly Func<string,string> _localize;
    private readonly Dictionary<(int Topic,string Question),int> _known = [];
    public LiteraryCalibrationLabels(Func<string,string> localize)
    {
        _localize=localize;
        void Add(Func<string,string> text)
        {
            foreach(var q in LiteraryInterviewCatalog.Questions.Where(q=>q.Number is >=4 and <=34))
                _known.TryAdd((q.Topic,text(q.Key).Trim()),q.Number);
        }
        Add(localize);
        foreach(var language in new[]{"ru","en"})
        { var source=new LocalizationService(); source.Load(language); Add(source.T); }
    }
    public string Get(CalibrationField field)
    {
        if(field.Adaptive==true) return field.Label;
        var step=field.Step;
        if(step is null && _known.TryGetValue((field.Topic,field.Label.Trim()),out var matched)) step=matched;
        if(step is >=4 and <=34) return _localize("Literary.Calibration.Field"+step);
        return string.IsNullOrWhiteSpace(field.Label)?_localize("Literary.Calibration.Other"):field.Label;
    }
}
