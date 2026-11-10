using System.Text.Json;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.Recognition;
using RemoteConnectHelper.V2.Connections;
using RemoteConnectHelper.V2.Domain;

namespace RemoteConnectHelper.V2.Diagnostics;
internal static class NotificationProbe
{
    internal static int Fixture(string path,string output)
    {
        Directory.CreateDirectory(output);int exit=1;
        var app=new System.Windows.Application{ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};
        app.Dispatcher.BeginInvoke(new Action(async()=>{
            System.Windows.Window? window=null;
            try{
                var bitmap=new System.Windows.Media.Imaging.BitmapImage();bitmap.BeginInit();bitmap.CacheOption=System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;bitmap.UriSource=new Uri(Path.GetFullPath(path));bitmap.EndInit();
                var canvas=new System.Windows.Controls.Canvas{Background=System.Windows.Media.Brushes.White};
                var picture=new System.Windows.Controls.Image{Source=bitmap,Stretch=System.Windows.Media.Stretch.Fill};canvas.Children.Add(picture);
                window=new(){Title="灰Desk关注通知关闭验收",WindowStyle=System.Windows.WindowStyle.None,ResizeMode=System.Windows.ResizeMode.NoResize,Topmost=true,ShowInTaskbar=false,Left=30,Top=30,Content=canvas};window.Show();
                var transform=System.Windows.PresentationSource.FromVisual(window)!.CompositionTarget!.TransformToDevice;
                window.Width=picture.Width=bitmap.PixelWidth/transform.M11;window.Height=picture.Height=bitmap.PixelHeight/transform.M22;
                using var source=new System.Drawing.Bitmap(path);
                var doc=await NegativeClientStatus.StatusOcr.ReadAsync(File.ReadAllBytes(path),0,false,CancellationToken.None);
                var sourceAction=NotificationPopup.Close(source,doc)??throw new InvalidOperationException("Original notification X not located");
                int clicked=0;
                var close=new System.Windows.Controls.Button{Width=18/transform.M11,Height=18/transform.M22,Opacity=0};
                System.Windows.Controls.Canvas.SetLeft(close,(sourceAction.Bounds.CenterX-9)/transform.M11);System.Windows.Controls.Canvas.SetTop(close,(sourceAction.Bounds.CenterY-9)/transform.M22);
                close.Click+=(_,_)=>{clicked++;picture.Visibility=System.Windows.Visibility.Hidden;close.Visibility=System.Windows.Visibility.Hidden;};canvas.Children.Add(close);
                window.Activate();await Task.Delay(180);
                var h=new System.Windows.Interop.WindowInteropHelper(window).Handle;
                InputSimulator.BringToFront(h);await Task.Delay(100);
                var position=window.PointToScreen(new System.Windows.Point(0,0));
                var win=new ScreenOps.Win{Hwnd=h,X=(int)position.X,Y=(int)position.Y,W=bitmap.PixelWidth,H=bitmap.PixelHeight};
                using var screen=ScreenOps.CaptureClient(win);using var bytes=new MemoryStream();screen.Save(bytes,System.Drawing.Imaging.ImageFormat.Png);
                var actual=await NegativeClientStatus.StatusOcr.ReadAsync(bytes.ToArray(),0,false,CancellationToken.None);
                var action=NotificationPopup.Close(screen,actual)??throw new InvalidOperationException("Live fixture X not located");
                var protectedDoc=actual with{Blocks=actual.Blocks.Concat([new("auth","扫码登录",new Box(10,10,80,25),.99f,"fixture","original")]).ToArray()};
                if(NotificationPopup.Close(screen,protectedDoc)!=null)throw new InvalidOperationException("QR login was accepted as optional notification");
                using var missing=(System.Drawing.Bitmap)screen.Clone();using(var graphics=System.Drawing.Graphics.FromImage(missing))graphics.Clear(System.Drawing.Color.White);
                if(NotificationPopup.Close(missing,actual)!=null)throw new InvalidOperationException("Missing X was guessed");
                var context=new ConnectionContext(RemoteAppKind.Sunlogin){MainWindow=h};
                try{await Task.Run(()=>{using var protection=new InputProtection(context,h);ScreenOps.ClickVerifiedScreen(h,win.X+(int)action.Bounds.CenterX,win.Y+(int)action.Bounds.CenterY,120);protection.Check();});}finally{context.Cts.Dispose();}
                await Task.Delay(120);
                if(clicked!=1)throw new InvalidOperationException("Actual close click count="+clicked);
                File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=true,Clicked=clicked,Action=action,ProtectedLoginPreserved=true,MissingXRejected=true,Scope="Actual PP-OCR and mouse click on a private fixture displaying the reported notification screenshot; not a live Sunlogin popup"}));exit=0;
            }catch(Exception ex){File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=false,Error=ex.ToString()}));}
            finally{window?.Close();app.Shutdown();}
        }));app.Run();return exit;
    }
    internal static int Image(string path,string output)
    {
        Directory.CreateDirectory(output);
        using var image=new System.Drawing.Bitmap(path);
        var doc=NegativeClientStatus.StatusOcr.ReadAsync(File.ReadAllBytes(path),0,false,CancellationToken.None).GetAwaiter().GetResult();
        var action=NotificationPopup.Close(image,doc);
        File.WriteAllText(Path.Combine(output,"notification.json"),JsonSerializer.Serialize(new{Recognized=NotificationPopup.Recognized(doc),Action=action,Document=doc},new JsonSerializerOptions{WriteIndented=true}));
        return action==null?1:0;
    }
    internal static int Live(string output)
    {
        Directory.CreateDirectory(output);
        var win=ScreenOps.FindWindow(["AweSun","SunloginClient"],["FLUTTER_RUNNER_WIN32_WINDOW"],["FlutterMultiWindow"]);
        if(win==null)return 1;
        var context=new ConnectionContext(RemoteAppKind.Sunlogin){MainWindow=win.Hwnd};
        try{
            ScreenOps.EnsureForeground(win,context);
            StartupScreenRecovery.PrepareVisibleMain(win,context);
            using(var before=ScreenOps.CaptureClient(win))before.Save(Path.Combine(output,"before-private.png"));
            bool recovered=StartupScreenRecovery.TryPrepare(context,out var main);
            using(var after=ScreenOps.CaptureClient(main??win))after.Save(Path.Combine(output,"after-private.png"));
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Recovered=recovered,context.StartupNeedsManual}));
            return recovered?0:1;
        }finally{context.Cts.Dispose();}
    }
}
