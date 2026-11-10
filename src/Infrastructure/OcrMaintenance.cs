using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace RemoteConnectHelper.V2.Infrastructure;
internal static class OcrMaintenance
{
    internal static void ApplyVerifiedCompatibility(bool reload=false){
        try{
            if(!reload&&!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR")))return;
            string local=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GrayDesk20");
            string file=Path.Combine(local,"ocr-compat.json");if(!File.Exists(file))return;
            using var json=JsonDocument.Parse(File.ReadAllText(file));
            string expected=json.RootElement.GetProperty("ExecutableHash").GetString()??"";
            string bundle=Path.GetFullPath(json.RootElement.GetProperty("Bundle").GetString()??"");
            string cache=Path.GetFullPath(Path.Combine(local,"CompatCache"))+Path.DirectorySeparatorChar;
            if(!bundle.StartsWith(cache,StringComparison.OrdinalIgnoreCase)||!Directory.Exists(bundle))return;
            using var exe=File.OpenRead(Environment.ProcessPath!);
            if(Convert.ToHexString(SHA256.HashData(exe))!=expected)return;
            Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR",bundle);
        }catch(Exception ex){Log.Write("OCR兼容配置未启用 "+ex.GetType().Name);}
    }
    internal static async Task ShowAsync(){
        var cancellation=new CancellationTokenSource();
        var label=new System.Windows.Controls.TextBlock{Text="正在用测试图片检查 PP-OCR…",Margin=new System.Windows.Thickness(18),TextWrapping=System.Windows.TextWrapping.Wrap};
        var button=new System.Windows.Controls.Button{Content="取消",Width=90,Height=30,Margin=new System.Windows.Thickness(12),HorizontalAlignment=System.Windows.HorizontalAlignment.Right};
        var panel=new System.Windows.Controls.DockPanel();System.Windows.Controls.DockPanel.SetDock(button,System.Windows.Controls.Dock.Bottom);panel.Children.Add(button);panel.Children.Add(label);
        var window=new System.Windows.Window{Title="灰Desk · OCR自检与修复",Width=410,Height=210,Content=panel,Topmost=true,WindowStartupLocation=System.Windows.WindowStartupLocation.CenterScreen};
        bool closed=false;window.Closed+=(_,_)=>{closed=true;cancellation.Cancel();cancellation.Dispose();};button.Click+=(_,_)=>window.Close();window.Show();
        string root=Path.Combine(Log.Root,"diagnostics","ocr-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{
            var info=new ProcessStartInfo(Environment.ProcessPath!,"--verify-ocr-isolation \""+root+"\""){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=AppContext.BaseDirectory};
            info.Environment["GRAYDESK_DIAGNOSTIC_ROOT"]=Path.Combine(root,"private");
            using var process=Process.Start(info)??throw new IOException("无法启动OCR自检");
            try{await process.WaitForExitAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(90),cancellation.Token);}
            finally{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}
            if(closed)return;
            string report=Path.Combine(root,"OCR隔离验收.json");bool passed=false;
            if(File.Exists(report)){using var json=JsonDocument.Parse(File.ReadAllText(report));passed=process.ExitCode==0&&json.RootElement.GetProperty("Passed").GetBoolean();}
            if(passed){label.Text="PP-OCR自检通过，识别引擎可用。\n结果保存在本地日志的 diagnostics 目录。";button.Content="关闭";return;}
            label.Text="PP-OCR自检未通过。可尝试启用随包提供的独立兼容运行库。";
            if(System.Windows.MessageBox.Show(window,"尝试OCR兼容修复？会先隔离测试，通过后重启灰Desk，不修改系统运行库。","灰Desk OCR修复",System.Windows.MessageBoxButton.YesNo,System.Windows.MessageBoxImage.Question)!=System.Windows.MessageBoxResult.Yes){button.Content="关闭";return;}
            string payload=Path.Combine(AppContext.BaseDirectory,"Tools","OcrRepair"),script=Path.Combine(payload,"repair.ps1");
            if(!File.Exists(script)||!File.Exists(Path.Combine(payload,"app.sha256")))throw new IOException("修复文件不完整，请使用完整安装包");
            var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true};
            foreach(string arg in new[]{"-NoProfile","-ExecutionPolicy","Bypass","-WindowStyle","Hidden","-File",script,"-PayloadRoot",payload,"-SourceExe",Environment.ProcessPath!})start.ArgumentList.Add(arg);
            Process.Start(start);window.Close();
        }catch(OperationCanceledException){}
        catch(Exception ex){Log.Write("OCR自检未完成 "+ex.GetType().Name);if(!closed){label.Text="自检未完成："+ex.Message+"\n请打开日志目录查看 diagnostics 结果。";button.Content="关闭";}}
    }
}
