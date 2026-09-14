namespace AIHub.Services;

public sealed partial class LiteraryChatRuntime
{
    public async Task<CalibrationResult> CalibrateAsync(CalibrationRequest request,CancellationToken token)
    {
        CalibrationResult? result=null;
        await StructuredAnalysisAsync(LiteraryCalibrationAnalysis.Messages(request),_=>{},token,
            LiteraryCalibrationAnalysis.Schema,"Calibration",
            new { request.Check, request.WholeProject, request.Hash, ids=request.Fields.Select(f=>f.Id).ToArray() },
            json => result=LiteraryCalibrationAnalysis.Parse(json,request.Fields));
        return result!;
    }
}
