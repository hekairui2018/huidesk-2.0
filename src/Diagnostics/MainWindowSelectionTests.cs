using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteConnectHelper.Automation;

namespace RemoteConnectHelper.V2.Diagnostics;
internal static class MainWindowSelectionTests
{
    internal static int Run(string output){
        Directory.CreateDirectory(output);int exit=1;var rows=new List<object>();
        var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Dispatcher.BeginInvoke(new Action(async()=>{
            Window? main=null,hidden=null,dialog=null,session=null;
            void Check(bool ok,string name){rows.Add(new{Name=name,Passed=ok});if(!ok)throw new InvalidOperationException(name);}
            try{
                hidden=new Window{Title="向日葵 隐藏占位测试",Width=1000,Height=700,ShowInTaskbar=false};new WindowInteropHelper(hidden).EnsureHandle();
                main=new Window{Title="向日葵 原生主界面测试",Width=580,Height=360,Topmost=true,ShowActivated=false,ShowInTaskbar=false,Content=new System.Windows.Controls.TextBlock{Text="主窗口查找验收\n原生窗口类型不在旧白名单内",FontSize=24,Margin=new Thickness(25)}};main.Show();await Task.Delay(120);
                var h=new WindowInteropHelper(main).Handle;
                var found=ScreenOps.FindForProcesses(new HashSet<int>{Environment.ProcessId},["AweSun"],["NoMatchingLegacyClass"],["FlutterMultiWindow"]);
                Check(found?.Hwnd==h,"Real native WPF main window is found despite changed class and earlier hidden placeholder");
                dialog=new Window{Title="向日葵 广告弹窗测试",Owner=main,Width=900,Height=600,ShowActivated=false,ShowInTaskbar=false};dialog.Show();
                found=ScreenOps.FindForProcesses(new HashSet<int>{Environment.ProcessId},["AweSun"],["NoMatchingLegacyClass"],[]);
                Check(found?.Hwnd==h,"Larger owned popup does not replace actual main window");dialog.Close();dialog=null;
                session=new Window{Title="ToDesk 123 456 789",Width=850,Height=600,ShowActivated=false,ShowInTaskbar=false};session.Show();
                Check(ScreenOps.FindForProcesses(new HashSet<int>{Environment.ProcessId},["ToDesk"],["NoMatchingLegacyClass"],[])==null,"Device-number session title is excluded from real enumeration");session.Close();session=null;
                main.UpdateLayout();var bitmap=new RenderTargetBitmap((int)main.ActualWidth,(int)main.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(main);
                using(var stream=File.Create(Path.Combine(output,"原生主窗口查找验收.png"))){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(stream);}
                exit=0;
            }catch(Exception ex){rows.Add(new{Name="Fixture",Passed=false,Error=ex.Message});}
            finally{session?.Close();dialog?.Close();main?.Close();hidden?.Close();File.WriteAllText(Path.Combine(output,"主窗口选择验收.json"),JsonSerializer.Serialize(new{Passed=exit==0,Scope="Real owned native windows and actual EnumWindows; no remote client operations",Tests=rows},new JsonSerializerOptions{WriteIndented=true}));app.Shutdown();}
        }));app.Run();return exit;
    }
}
