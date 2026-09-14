using AIHub.Models;

namespace AIHub.Services;

public sealed record LiteraryProjectParameter(int Step, string Label, string Value);

public static class LiteraryProjectParameters
{
    public static IReadOnlyList<LiteraryProjectParameter> Read(LiteraryProject project, Func<string,string> l)
    {
        var labels=new LiteraryCalibrationLabels(l);
        var fields=new LiteraryCalibrationDocument(project.CreationBrief).Fields;
        var result=new List<LiteraryProjectParameter>();
        foreach(var step in new[]{5,6,7,26,27,28,29,30})
        {
            var field=fields.LastOrDefault(f=>labels.StepNumber(f)==step);
            // An intentionally cleared calibrated value must not resurrect the creator's old value.
            var value=field?.Value ?? (step switch
            {
                5=>l("Literary.Form."+project.Form),
                6=>string.Join(", ",project.Genres.Select(g=>l("Literary.Interview.Genre."+g))
                    .Concat(string.IsNullOrWhiteSpace(project.CustomGenres)?[]:new[]{project.CustomGenres})),
                _=>""
            });
            result.Add(new(step,l("Literary.Calibration.Field"+step),string.IsNullOrWhiteSpace(value)?l("Literary.Parameters.Unspecified"):value));
        }
        return result;
    }
}
