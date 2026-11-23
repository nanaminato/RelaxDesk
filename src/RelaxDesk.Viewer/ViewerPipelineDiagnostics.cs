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
        "cpu-decode" or "gpu-decode" => "视频解码失败，连接已停止。请保留两端诊断日志。",
        "cpu-submit" => "画面显示失败，连接已停止。请保留 Viewer 诊断日志。",
        "gpu-renderer-init" => "无法初始化画面显示，连接已停止。请保留 Viewer 诊断日志。",
        "gpu-present" => "画面呈现失败，连接已停止。请保留 Viewer 诊断日志。",
        "media-validate" => "视频数据无效，连接已停止。请保留两端诊断日志。",
        "viewer-update" => "界面更新失败，连接已停止。请保留 Viewer 诊断日志。",
        _ => null
    };
}
