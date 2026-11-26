namespace RelaxDesk.Core;

public static class AudioConsent
{
    public static bool AllowsAudio(string decision)=>decision is "APPROVE_AUDIO" or "APPROVE_CLIPBOARD_AUDIO" or "APPROVE_FILES_AUDIO" or "APPROVE_ALL_AUDIO";
    public static string Base(string decision)=>AllowsAudio(decision)?decision=="APPROVE_AUDIO"?"APPROVE":decision[..^6]:decision;
    public static string Command(bool allow,bool clipboard,bool files,bool audio)=>!allow?"DENY":(clipboard&&files?"APPROVE_ALL":files?"APPROVE_FILES":clipboard?"APPROVE_CLIPBOARD":"APPROVE")+(audio?"_AUDIO":"");
    public static ConsentDecision? Parse(string command)=>command switch
    {
        "APPROVE"=>ConsentDecision.ScreenAndInput,"APPROVE_CLIPBOARD"=>ConsentDecision.Clipboard,"APPROVE_FILES"=>ConsentDecision.Files,"APPROVE_ALL"=>ConsentDecision.All,
        "APPROVE_AUDIO"=>ConsentDecision.Audio,"APPROVE_CLIPBOARD_AUDIO"=>ConsentDecision.ClipboardAudio,"APPROVE_FILES_AUDIO"=>ConsentDecision.FilesAudio,"APPROVE_ALL_AUDIO"=>ConsentDecision.AllAudio,"DENY"=>ConsentDecision.Deny,_=>null
    };
    public static string Command(ConsentDecision decision)=>decision switch
    {
        ConsentDecision.ScreenAndInput=>"APPROVE",ConsentDecision.Clipboard=>"APPROVE_CLIPBOARD",ConsentDecision.Files=>"APPROVE_FILES",ConsentDecision.All=>"APPROVE_ALL",
        ConsentDecision.Audio=>"APPROVE_AUDIO",ConsentDecision.ClipboardAudio=>"APPROVE_CLIPBOARD_AUDIO",ConsentDecision.FilesAudio=>"APPROVE_FILES_AUDIO",ConsentDecision.AllAudio=>"APPROVE_ALL_AUDIO",_=>"DENY"
    };
}
