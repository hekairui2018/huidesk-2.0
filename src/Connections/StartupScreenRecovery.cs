using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.Recognition;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Infrastructure;

namespace RemoteConnectHelper.V2.Connections;
internal sealed record StartupAction(string Label,Box Bounds);
internal static class StartupScreenRecovery
{
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr h,out uint id);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr h,StringBuilder s,int n);
    [DllImport("user32.dll")]private static extern bool GetClientRect(IntPtr h,out Rect r);
    [DllImport("user32.dll")]private static extern bool ClientToScreen(IntPtr h,ref Point p);
    [DllImport("user32.dll")]private static extern IntPtr GetWindow(IntPtr h,uint command);
    private struct Rect{public int L,T,R,B;}
    private struct Point{public int X,Y;}
    private static string Flat(string text)=>Regex.Replace(text,@"\s+","");
    internal static bool Protected(OcrDocument doc)=>Regex.IsMatch(Flat(string.Concat(doc.Blocks.Select(b=>b.Text))),"请输入(?:连接|访问|远程|设备|临时|安全|独立|验证|您的|正确的)?(?:密码|验证码)|扫码登录|账号登录|身份验证|安全验证|等待.{0,8}(同意|授权)|用户协议|隐私政策|防诈骗|风险提示|确认连接");
    internal static bool MainFields(OcrDocument doc,RemoteAppKind kind){
        string text=Flat(string.Concat(doc.Blocks.Select(b=>b.Text)));
        return kind==RemoteAppKind.Sunlogin
            ? Regex.IsMatch(text,"远程协助他人|远程控制设备")&&Regex.IsMatch(text,"伙伴识别码|伙伴设备|验证码")
            : text.Contains("远程控制设备")&&Regex.IsMatch(text,"设备代码|输入.*代码");
    }
    internal static StartupAction? Choose(OcrDocument doc){
        string text=Flat(string.Concat(doc.Blocks.Select(b=>b.Text)));
        if(Protected(doc))return null;
        if(!NotificationPopup.Recognized(doc)&&text.Contains("向日葵远程控制")&&!MainFields(doc,RemoteAppKind.Sunlogin)){
            var navigation=Labels(doc).Where(b=>b.Confidence>=.8f&&Flat(b.Text)=="远程协助"
                &&b.Layout.X>=0&&b.Layout.Right<doc.Width*.35&&b.Layout.Y>40&&b.Layout.Bottom<doc.Height*.6).ToArray();
            if(navigation.Length==1)return new("返回远程协助",navigation[0].Layout);
        }
        bool failure=doc.Width<=650&&doc.Height<=400&&Regex.IsMatch(text,"不能连接本机识别码|识别码不存在|识别码错误|验证码错误|密码错误|设备不在线|设备已离线|目标设备离线");
        bool welcome=doc.Width>=600&&doc.Height>=400&&text.Contains("贝锐向日葵")&&text.Contains("国民远控守护国民安全");
        if(!failure&&!welcome&&!Regex.IsMatch(text,"会员|福利|限时|优惠|促销|广告|活动|新人礼包"))return null;
        string dismiss=welcome?"^立即进入$":failure?"^(确定|关闭)$":"^(关闭|跳过|暂不|以后再说|稍后再说|稍后提醒)$";
        var actions=Labels(doc).Where(b=>b.Confidence is >=.8f&&Regex.IsMatch(Flat(b.Text),dismiss)
            &&b.Layout.Width>=12&&b.Layout.Width<=200&&b.Layout.Height>=10&&b.Layout.Height<=65
            &&b.Layout.X>=0&&b.Layout.Y>=0&&b.Layout.Right<=doc.Width&&b.Layout.Bottom<=doc.Height).ToArray();
        return actions.Length==1?new(Flat(actions[0].Text),actions[0].Layout):null;
    }
    internal static IEnumerable<OcrTextBlock> Labels(OcrDocument doc){
        var rows=new List<List<OcrTextBlock>>();
        foreach(var block in doc.Blocks.OrderBy(b=>b.Layout.CenterY)){
            var row=rows.FirstOrDefault(r=>Math.Abs(r.Average(b=>b.Layout.CenterY)-block.Layout.CenterY)<=Math.Max(4,Math.Min(r.Average(b=>b.Layout.Height),block.Layout.Height)*.35));
            if(row==null)rows.Add([block]);else row.Add(block);
        }
        foreach(var row in rows){
            var groups=new List<List<OcrTextBlock>>();
            foreach(var block in row.OrderBy(b=>b.Layout.X)){
                if(groups.Count==0||block.Layout.X-groups[^1][^1].Layout.Right>Math.Max(8,Math.Min(block.Layout.Height,groups[^1][^1].Layout.Height)*.65))groups.Add([block]);
                else groups[^1].Add(block);
            }
            foreach(var group in groups){
                var bounds=Box.Union(group.Select(b=>b.Layout));
                yield return new("label",string.Concat(group.Select(b=>b.Text)),bounds,group.All(b=>b.Confidence.HasValue)?group.Min(b=>b.Confidence):null,doc.Engine,doc.Variant);
            }
        }
    }
    internal static bool PermittedWindow(string cls,string title,bool ownerIsMain,bool isMain){
        if(Regex.IsMatch(cls,"FlutterMultiWindow|ORAY_FLUTTER_VIEW_CONTAINER|RemoteDesktop|Session",RegexOptions.IgnoreCase))return false;
        if(isMain)return true;
        if(ownerIsMain&&cls is "#32770" or "OrayUI")return true;
        return Regex.IsMatch(cls,"FLUTTER_RUNNER_WIN32_WINDOW|H-SMILE-FRAME|SunloginClient",RegexOptions.IgnoreCase)
            &&Regex.IsMatch(title,"向日葵|Sunlogin|AweSun|ToDesk",RegexOptions.IgnoreCase)&&!Regex.IsMatch(title,@"\d{6,}");
    }
    internal static bool TryPrepare(ConnectionContext? context,out ScreenOps.Win? recovered){
        recovered=null;if(context==null)return false;
        var clock=Stopwatch.StartNew();int clicks=0;
        try{
            while(clock.ElapsedMilliseconds<8000){
                context.ThrowIfCancellationRequested();var h=GetForegroundWindow();if(h==IntPtr.Zero)return false;
                GetWindowThreadProcessId(h,out uint id);using var process=Process.GetProcessById((int)id);
                string[] names=context.Kind==RemoteAppKind.Sunlogin?["AweSun","SunloginClient"]:["ToDesk"];
                if(!names.Contains(process.ProcessName,StringComparer.OrdinalIgnoreCase))return false;
                var cls=new StringBuilder(128);var title=new StringBuilder(256);GetClassName(h,cls,cls.Capacity);GetWindowText(h,title,title.Capacity);
                if(!PermittedWindow(cls.ToString(),title.ToString(),context.MainWindow!=IntPtr.Zero&&GetWindow(h,4)==context.MainWindow,h==context.MainWindow))return false;
                if(!GetClientRect(h,out var rect)||rect.R<150||rect.B<80||rect.R>8192||rect.B>8192)return false;
                var origin=new Point();if(!ClientToScreen(h,ref origin))return false;
                var win=new ScreenOps.Win{Hwnd=h,X=origin.X,Y=origin.Y,W=rect.R,H=rect.B};
                using var bitmap=ScreenOps.CaptureClient(win);using var stream=new MemoryStream();bitmap.Save(stream,System.Drawing.Imaging.ImageFormat.Png);
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(context.Token);timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var doc=NegativeClientStatus.StatusOcr.ReadAsync(stream.ToArray(),0,false,timeout.Token).GetAwaiter().GetResult();
                if(Protected(doc)){context.StartupNeedsManual=true;Log.Write("启动页需登录/授权/安全确认，保留人工处理");return false;}
                // A clear advertisement dismissal is handled before accepting background main-screen labels.
                var action=NotificationPopup.Close(bitmap,doc)??Choose(doc);
                if(action!=null&&clicks<2){
                    using var protection=InputProtection.Begin(context);
                    protection.AllowReturnToMainWindow();
                    context.ThrowIfCancellationRequested();if(GetForegroundWindow()!=h)return false;
                    var currentOrigin=new Point();
                    if(!GetClientRect(h,out var currentRect)||!ClientToScreen(h,ref currentOrigin)||currentOrigin.X!=win.X||currentOrigin.Y!=win.Y||currentRect.R!=win.W||currentRect.B!=win.H)return false;
                    ScreenOps.ClickVerifiedScreen(h,win.X+(int)action.Bounds.CenterX,win.Y+(int)action.Bounds.CenterY,120);
                    protection.Check();
                    clicks++;Log.Write("启动页PP-OCR确认并点击 action="+action.Label);
                    if(!context.Sleep(250))return false;continue;
                }
                if(NotificationPopup.Recognized(doc)){
                    context.StartupNeedsManual=true;
                    context.StartupBlockingReason="向日葵微信关注通知遮挡输入区，未确认关闭按钮，请关闭通知后重试";
                    Log.Write(context.StartupBlockingReason);return false;
                }
                if(action==null&&MainFields(doc,context.Kind)){recovered=win;Log.Write("启动页PP-OCR已确认主界面");return true;}
                return false;
            }
        }catch(OperationCanceledException){context.ThrowIfCancellationRequested();Log.Write("启动页PP-OCR检查超时");}
        catch(Exception ex){Log.Write("启动页PP-OCR检查未完成 "+ex.GetType().Name);}
        return false;
    }
    internal static void PrepareVisibleMain(ScreenOps.Win win,ConnectionContext? context){
        if(context==null)return;
        if(TryPrepare(context,out var main)&&main!=null){
            win.Hwnd=main.Hwnd;win.X=main.X;win.Y=main.Y;win.W=main.W;win.H=main.H;
            context.MainWindow=win.Hwnd;
        }
        if(context.StartupNeedsManual)throw new InvalidOperationException(context.StartupBlockingReason??"客户端正显示登录、密码或安全确认，请先处理弹窗后重试");
    }
}
