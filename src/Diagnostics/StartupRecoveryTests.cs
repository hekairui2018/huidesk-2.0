using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.Recognition;
using RemoteConnectHelper.V2.Connections;
using RemoteConnectHelper.V2.Domain;

namespace RemoteConnectHelper.V2.Diagnostics;
internal static class StartupRecoveryTests
{
    [DllImport("user32.dll")]private static extern bool ClientToScreen(IntPtr h,ref Point p);
    [DllImport("user32.dll")]private static extern bool GetClientRect(IntPtr h,out Rect r);
    private struct Point{public int X,Y;}
    private struct Rect{public int L,T,R,B;}
    internal static int Run(string output){
        Directory.CreateDirectory(output);var rows=new List<object>();int exit=1;
        var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Dispatcher.BeginInvoke(new Action(async()=>{
            Window? fixture=null;
            void Check(bool ok,string name){rows.Add(new{Name=name,Passed=ok});if(!ok)throw new InvalidOperationException(name);}
            OcrDocument Doc(string label="关闭",float confidence=.99f)=>new([new("ad","会员限时优惠",new Box(40,40,200,40),.99f,"fixture","original"),new("close",label,new Box(320,200,100,35),confidence,"fixture","original")],480,300,"fixture","original");
            try{
                var doc=Doc();Check(StartupScreenRecovery.Choose(doc)?.Label=="关闭","Promotion plus one explicit close action is selected");
                Check(StartupScreenRecovery.Choose(Doc("立即购买"))==null,"Purchase button is never a dismissal");
                Check(StartupScreenRecovery.Choose(Doc(confidence:.5f))==null,"Low-confidence label is not clicked");
                Check(StartupScreenRecovery.Choose(doc with{Blocks=doc.Blocks.Concat([new OcrTextBlock("another","跳过",new Box(180,200,70,35),.99f,"fixture","original")]).ToArray()})==null,"Multiple dismissal targets remain manual");
                Check(StartupScreenRecovery.Choose(doc with{Width=100})==null,"Out-of-bounds action is rejected");
                var split=doc with{Blocks=[doc.Blocks[0],new("a","关",new Box(320,200,25,35),.99f,"fixture","original"),new("b","闭",new Box(346,200,25,35),.99f,"fixture","original")]};
                Check(StartupScreenRecovery.Choose(split)?.Label=="关闭","Adjacent same-line OCR characters form a button label");
                Check(StartupScreenRecovery.Choose(split with{Blocks=split.Blocks.Concat([new OcrTextBlock("suffix","广告",new Box(372,200,50,35),.99f,"fixture","original")]).ToArray()})==null,"Close substring inside longer text is not a button");
                foreach(string text in new[]{"请输入连接密码","请输入验证码","扫码登录","账号登录","身份验证","安全验证","等待对方同意","等待授权","用户协议","隐私政策","防诈骗","风险提示","确认连接"}){
                    var protectedDoc=doc with{Blocks=doc.Blocks.Concat([new OcrTextBlock("protected",text,new Box(40,100,240,35),.99f,"fixture","original")]).ToArray()};
                    Check(StartupScreenRecovery.Protected(protectedDoc)&&StartupScreenRecovery.Choose(protectedDoc)==null,"Protected prompt preserved: "+text);
                }
                Check(!StartupScreenRecovery.PermittedWindow("FlutterMultiWindow","向日葵",true,true),"Remote session rendering class is never captured by startup recovery");
                Check(!StartupScreenRecovery.PermittedWindow("OtherApplication","向日葵",false,false),"Unrelated application window is rejected");
                Check(StartupScreenRecovery.PermittedWindow("#32770","广告",true,false),"Owned client dialog is eligible for OCR inspection");
                Check(!StartupScreenRecovery.PermittedWindow("#32770","广告",false,false),"Unowned dialog is rejected");
                Check(StartupScreenRecovery.MainFields(doc with{Blocks=[new("main","远程协助他人 伙伴识别码 验证码",new Box(10,10,300,35),.99f,"fixture","original")]},RemoteAppKind.Sunlogin),"Recovered main screen requires target input labels");
                Check(!StartupScreenRecovery.MainFields(doc,RemoteAppKind.Sunlogin),"Advertisement alone is not a main screen");
                int clicked=0,downs=0,ups=0;
                var panel=new System.Windows.Controls.Grid{Background=System.Windows.Media.Brushes.White};
                panel.Children.Add(new System.Windows.Controls.TextBlock{Text="会员限时优惠",FontSize=32,Foreground=System.Windows.Media.Brushes.Black,Margin=new Thickness(35),VerticalAlignment=VerticalAlignment.Top});
                var close=new System.Windows.Controls.Button{Content="关闭",FontSize=28,Width=120,Height=54,Margin=new Thickness(25),VerticalAlignment=VerticalAlignment.Bottom,HorizontalAlignment=System.Windows.HorizontalAlignment.Right};
                close.Click+=(_,_)=>clicked++;panel.Children.Add(close);
                fixture=new Window{Title="灰Desk自建启动页验收",Width=500,Height=330,Left=60,Top=80,Topmost=true,Content=panel,WindowStyle=WindowStyle.None,ShowInTaskbar=false};
                fixture.PreviewMouseDown+=(_,_)=>downs++;fixture.PreviewMouseUp+=(_,_)=>ups++;fixture.Show();fixture.Activate();await Task.Delay(150);var h=new WindowInteropHelper(fixture).Handle;InputSimulator.BringToFront(h);await Task.Delay(100);
                var origin=new Point();ClientToScreen(h,ref origin);GetClientRect(h,out var rect);
                var win=new ScreenOps.Win{Hwnd=h,X=origin.X,Y=origin.Y,W=rect.R,H=rect.B};
                using var image=ScreenOps.CaptureClient(win);image.Save(Path.Combine(output,"PP-OCR启动页鼠标点击验收.png"),System.Drawing.Imaging.ImageFormat.Png);
                using var stream=new MemoryStream();image.Save(stream,System.Drawing.Imaging.ImageFormat.Png);
                var actual=await NegativeClientStatus.StatusOcr.ReadAsync(stream.ToArray(),0,false,CancellationToken.None);
                File.WriteAllText(Path.Combine(output,"fixture-ocr.json"),JsonSerializer.Serialize(actual));
                var action=StartupScreenRecovery.Choose(actual);Check(action?.Label=="关闭","Real PP-OCR locates close text in owned screenshot");
                await Task.Run(()=>{var context=new ConnectionContext(RemoteAppKind.Sunlogin){MainWindow=h};try{using var protection=new InputProtection(context,h);ScreenOps.ClickVerifiedScreen(h,origin.X+(int)action!.Bounds.CenterX,origin.Y+(int)action.Bounds.CenterY,80);protection.Check();}finally{context.Cts.Dispose();}});await Task.Delay(150);
                File.WriteAllText(Path.Combine(output,"fixture-click.json"),JsonSerializer.Serialize(new{clicked,downs,ups,over=close.IsMouseOver,enabled=close.IsEnabled,originX=origin.X,originY=origin.Y,box=action!.Bounds,expected=close.PointToScreen(new System.Windows.Point(close.ActualWidth/2,close.ActualHeight/2)),cursor=System.Windows.Forms.Cursor.Position,scale=System.Windows.PresentationSource.FromVisual(fixture)?.CompositionTarget?.TransformToDevice}));Check(clicked==1,"OCR coordinates click the actual owned close button exactly once");
                exit=0;
            }catch(Exception ex){rows.Add(new{Name="Fixture",Passed=false,Error=ex.Message});}
            finally{fixture?.Close();File.WriteAllText(Path.Combine(output,"启动页OCR恢复验收.json"),JsonSerializer.Serialize(new{Passed=exit==0,Scope="Real PP-OCR and mouse click on owned synthetic promotion window; no real remote software operated",Tests=rows},new JsonSerializerOptions{WriteIndented=true}));app.Shutdown();}
        }));app.Run();return exit;
    }
}
