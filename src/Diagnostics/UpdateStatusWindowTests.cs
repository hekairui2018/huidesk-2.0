using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteConnectHelper.V2.Infrastructure;

namespace RemoteConnectHelper.V2.Diagnostics;
internal static class UpdateStatusWindowTests
{
    internal static int Run(string output){
        Directory.CreateDirectory(output);int exit=1;string? error=null;
        var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Dispatcher.BeginInvoke(new Action(async()=>{
            Window? window=null;
            try{
                await OnlineUpdater.ShowAsync(new Window(),"2.2.0-test.20261028.1",_=>Task.FromException<AvailableUpdate?>(new HttpRequestException("fixture",null,HttpStatusCode.NotFound)));
                window=app.Windows.OfType<Window>().Single(w=>w.Title=="灰Desk · 在线更新");
                window.UpdateLayout();
                var panel=(System.Windows.Controls.DockPanel)window.Content;
                if(!panel.Children.OfType<System.Windows.Controls.TextBlock>().Single().Text.Contains("暂未上架"))throw new InvalidOperationException("404 guidance missing");
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(window);
                using var stream=File.Create(Path.Combine(output,"HTTP清单暂未上架.png"));var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(stream);exit=0;
            }catch(Exception ex){error=ex.Message;}
            finally{window?.Close();File.WriteAllText(Path.Combine(output,"更新状态窗口验收.json"),JsonSerializer.Serialize(new{Passed=exit==0,Scope="Real owned WPF update dialog with injected HTTP404; no installation or remote software operation",Error=error}));app.Shutdown();}
        }));app.Run();return exit;
    }
}
