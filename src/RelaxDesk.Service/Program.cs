using System.Security.Principal;
using RelaxDesk.Core;
using RelaxDesk.Service;
using RelaxDesk.Session;

if(args is ["--version"]){Console.WriteLine(BuildIdentity.Display);return;}
if(args.Length>0&&args[0] is "--install-service" or "--uninstall-service")
{
    try{await SetupCommands.Run(args);}
    catch(Exception error){Console.Error.WriteLine($"service_setup_failed error_type={error.GetType().Name} reason={error.Message}");Environment.ExitCode=1;if(args.Contains("--show-result",StringComparer.Ordinal))SetupResultDialog.Failure(error);}
    return;
}
if(args is ["--service","--owner-sid",var user])
{
    try{NativeService.Run(LocalAgentPipe.CanonicalUser(user));}
    catch(Exception error){Console.Error.WriteLine($"broker_start_failed error_type={error.GetType().Name} hresult=0x{error.HResult:X8}");Environment.ExitCode=1;}
    return;
}
if(args is ["--pipe-contract-server",var pipe,"--owner-sid",var owner])
{
    if(pipe==LocalAgentPipe.Name){Console.Error.WriteLine("Contract server requires a test pipe");Environment.ExitCode=1;return;}
    try
    {
        var broker=new SessionBrokerServer(owner,pipe,Console.WriteLine);
        _=Task.Run(async()=>{try{while(await Console.In.ReadLineAsync() is {} command){if(command=="STOP")break;if(command.StartsWith("INVALIDATE ",StringComparison.Ordinal)&&uint.TryParse(command[11..],out uint session))broker.InvalidateSession(session);}}finally{broker.RequestStop();}});
        await broker.Run(CancellationToken.None,()=>Console.WriteLine("BROKER_READY"));
    }
    catch(Exception error){Console.Error.WriteLine($"broker_contract_failed error_type={error.GetType().Name} hresult=0x{error.HResult:X8}");Environment.ExitCode=1;}
    return;
}
Console.WriteLine("RelaxDesk broker: --install-service --package <bundle> --owner-sid <user SID> [--automatic|--manual] [--start] [--what-if]; --uninstall-service --owner-sid <user SID> [--what-if]. Changes require explicit administrator elevation. No desktop permission or firewall change.");
Environment.ExitCode=2;
