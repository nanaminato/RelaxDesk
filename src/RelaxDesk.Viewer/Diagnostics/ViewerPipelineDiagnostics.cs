using RelaxDesk.Core.Localization;
using RelaxDesk.Core;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    SessionFailure? lastPipelineFailure;
    Action<string>? pipelineFaultForVerification;
    void InjectPipelineFault(string stage) { if (App.VerificationMode) pipelineFaultForVerification?.Invoke(stage); }
    bool PipelineAllowsReconnect => lastPipelineFailure is null || lastPipelineFailure.Stage is "media-read" or "control-read" or "control-write";
    string? PipelineFailureText => lastPipelineFailure?.Stage switch
    {
        "cpu-decode" or "gpu-decode" => Strings.Get("Ui.ViewerPipelineDiagnostics.Text442"),
        "cpu-submit" => Strings.Get("Ui.ViewerPipelineDiagnostics.Text386"),
        "gpu-renderer-init" => Strings.Get("Ui.ViewerPipelineDiagnostics.Text283"),
        "gpu-present" => Strings.Get("Ui.ViewerPipelineDiagnostics.Text385"),
        "media-validate" => Strings.Get("Ui.ViewerPipelineDiagnostics.Text440"),
        "viewer-update" => Strings.Get("Ui.ViewerPipelineDiagnostics.Text390"),
        _ => null
    };
}
