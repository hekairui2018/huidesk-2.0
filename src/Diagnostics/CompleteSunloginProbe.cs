using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.Recognition;

namespace RemoteConnectHelper.V2.Diagnostics;

// Explicit local acceptance only: caller supplies this computer's own ID.
// It exercises reopening, replacement, password verification and a real connect click.
internal static class CompleteSunloginProbe
{
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    internal static int Run(string localCode,string output)
    {
        if(!Regex.IsMatch(localCode,@"^\d{9,13}$"))throw new ArgumentException("Supply this computer's Sunlogin ID");
        Directory.CreateDirectory(output);
        bool reopened=false,closed=false,codeMatched=false,passwordMatched=false;
        ConnectResult result=ConnectResult.Fail("Not run");
        ConnectionContext context=null!;
        try{
            var initial=ScreenOps.FindWindow(["AweSun","SunloginClient"],["FLUTTER_RUNNER_WIN32_WINDOW"],["FlutterMultiWindow"]);
            if(initial==null)throw new InvalidOperationException("Open the verified local client before this acceptance test");
            var preparation=new ConnectionContext(RemoteAppKind.Sunlogin){MainWindow=initial.Hwnd};
            try{
                if(!ScreenOps.EnsureForeground(initial)&&
                    !(Connections.StartupScreenRecovery.TryPrepare(preparation,out _)&&ScreenOps.EnsureForeground(initial)))
                    throw new InvalidOperationException("Unable to inspect local client");
                using(var before=ScreenOps.CaptureClient(initial))before.Save(Path.Combine(output,"before-recovery-private.png"));
                Connections.StartupScreenRecovery.PrepareVisibleMain(initial,preparation);
            }finally{preparation.Cts.Dispose();}
            using(var image=ScreenOps.CaptureClient(initial)){
                var doc=ScreenOps.OcrAsync(image).GetAwaiter().GetResult();
                var lines=doc==null?[]:OcrGeo.BuildLines(doc);
                var remote=lines.FirstOrDefault(l=>l.Text.Contains("远程控制设备")||l.Text.Contains("远程协助他人"));
                var own=remote==null?null:OcrGeo.NumberField(lines,40,remote.Y-20);
                if(own==null||doc==null||!lines.Where(l=>Math.Abs(l.Y-own.Value.Cy)<16)
                    .Any(l=>Regex.Replace(l.Text,@"\D","").Contains(localCode)))
                    throw new InvalidOperationException("Supplied ID was not verified as this computer's own ID; no connection attempted");
            }
            PostMessage(initial.Hwnd,0x10,IntPtr.Zero,IntPtr.Zero);
            Thread.Sleep(400);closed=!IsWindowVisible(initial.Hwnd);
            if(!closed)throw new InvalidOperationException("Client did not close to tray");
            context=new ConnectionContext(RemoteAppKind.Sunlogin){InputProbe=(window,x,y,px,py)=>{
                reopened=IsWindowVisible(window.Hwnd);
                codeMatched=CoordOps.VerifyCode(window,localCode,x,y,context)==CodeReadback.Matched;
                if(!codeMatched)return ConnectResult.Fail("Device readback failed");
                // This known native build's reveal control is inspected for local test only.
                // Production discovery/input never uses these fixture coordinates.
                if(window.W!=800||window.H!=548)return ConnectResult.Fail("Unexpected native test layout");
                using var protection=InputProtection.BeginForTarget(context,window);
                bool ReadPassword(){
                    using var shown=ScreenOps.CaptureClient(window);
                    var doc=ScreenOps.OcrAsync(shown).GetAwaiter().GetResult();
                    bool matched=doc!=null&&OcrGeo.BuildLines(doc).Where(l=>Math.Abs(l.Y-y)<20)
                        .Any(l=>Regex.Replace(l.Text,@"\s","").Contains("123456"));
                    shown.Save(Path.Combine(output,"password-verified-private.png"));
                    return matched;
                }
                passwordMatched=ReadPassword();
                if(!passwordMatched){
                    ScreenOps.ClickClient(window,558,341,150);
                    passwordMatched=ReadPassword();
                }
                if(passwordMatched)ScreenOps.ClickClient(window,558,341,100);
                protection.Check();
                return passwordMatched?null:ConnectResult.Fail("Password readback failed");
            }};
            context.Cts.CancelAfter(TimeSpan.FromSeconds(40));
            result=new SunloginConnector().ConnectAsync(localCode,"123456",context).GetAwaiter().GetResult();
            Thread.Sleep(700);
            using var final=ScreenOps.CaptureRemote(context,out _,out _);
            final?.Save(Path.Combine(output,"after-connect-private.png"));
        }catch(Exception ex){result=ConnectResult.Fail(ex.GetType().Name+": "+ex.Message);}
        bool clicked=context?.ConnectClicked==true;
        bool passed=closed&&reopened&&codeMatched&&passwordMatched&&clicked&&result.Status==ConnectStatus.Launched;
        File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=passed,ClosedToTray=closed,Reopened=reopened,CodeMatched=codeMatched,PasswordMatched=passwordMatched,ConnectClicked=clicked,
            Status=result.Status.ToString(),result.Message,Scope="Local self-connection attempt; verifies input and click, not successful remote access"},new JsonSerializerOptions{WriteIndented=true}));
        context?.Cts.Dispose();return passed?0:1;
    }
}
