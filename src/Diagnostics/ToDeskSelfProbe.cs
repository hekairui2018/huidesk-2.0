using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.Recognition;
namespace RemoteConnectHelper.V2.Diagnostics;
internal static class ToDeskSelfProbe
{
    [DllImport("user32.dll")]static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
    internal static int Run(string output){
        Directory.CreateDirectory(output);bool matched=false;bool reopened=false;
        ConnectionContext ctx=null!;
        ctx=new ConnectionContext(RemoteAppKind.ToDesk){InputProbe=(win,x,y,px,py)=>{
            using(var entered=ScreenOps.CaptureClient(win))entered.Save(Path.Combine(output,"before-readback-private.png"));
            reopened=true;matched=CoordOps.VerifyCode(win,"682236496",x,y,ctx)==CodeReadback.Matched;
            if(!matched){
                CoordOps.Fill(win,x,y,"682236496",ctx,slow:true);
                matched=CoordOps.VerifyCode(win,"682236496",x,y,ctx)==CodeReadback.Matched;
            }
            return matched?null:ConnectResult.Fail("Readback failed");}};
        ConnectResult result;
        try{
            var win=ScreenOps.FindWindow(["ToDesk"],["H-SMILE-FRAME"],[])??throw new InvalidOperationException("No ToDesk main window");
            using(var image=ScreenOps.CaptureForInput(win,ctx)){
                var doc=ScreenOps.OcrAsync(image).GetAwaiter().GetResult()??throw new InvalidOperationException("No OCR");
                var lines=OcrGeo.BuildLines(doc);var heading=lines.First(l=>l.Text.Contains("远程控制设备"));
                if(!lines.Where(l=>l.Y<heading.Y).Any(l=>Regex.Replace(l.Text,@"\D","").Contains("682236496")))throw new InvalidOperationException("ID not verified as local; stopped");
            }
            PostMessage(win.Hwnd,0x10,IntPtr.Zero,IntPtr.Zero);Thread.Sleep(1500);
            ctx.Cts.CancelAfter(TimeSpan.FromSeconds(40));
            result=new ToDeskConnector().ConnectAsync("682236496","123456",ctx).GetAwaiter().GetResult();
            using var final=ScreenOps.CaptureRemote(ctx,out _,out _);final?.Save(Path.Combine(output,"after-click-private.png"));
        }catch(Exception ex){result=ConnectResult.Fail(ex.Message);}
        bool pass=reopened&&matched&&ctx.ConnectClicked&&result.Status==ConnectStatus.Launched;
        File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=pass,Reopened=reopened,CodeMatched=matched,ConnectClicked=ctx.ConnectClicked,Status=result.Status.ToString(),result.Message,Scope="Verified local self-ID only; no remote password dialog exercised"},new JsonSerializerOptions{WriteIndented=true}));
        ctx.Cts.Dispose();return pass?0:1;
    }
}
