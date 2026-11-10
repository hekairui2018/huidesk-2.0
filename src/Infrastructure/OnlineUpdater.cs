using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemoteConnectHelper.V2.Infrastructure;
internal sealed record AvailableUpdate(string Version,string Name,Uri Download,long Size,string Hash);
internal static class OnlineUpdater
{
    internal const string Repository="hekairui2018/RemoteConnectHelper";
    internal const string Endpoint="https://api.github.com/repos/"+Repository+"/releases?per_page=30";
    internal const string HttpHost="43nudj314064.vicp.fun";
    internal const string DefaultManifest="http://"+HttpHost+"/api/graydesk/updates/standard/win-x64";
    internal static bool AllowedTransport(Uri uri)=>string.IsNullOrEmpty(uri.UserInfo)&&
        (uri.Scheme=="https"||uri.Scheme=="http"&&uri.IsDefaultPort&&uri.Host.Equals(HttpHost,StringComparison.OrdinalIgnoreCase));
    internal static (int Major,int Minor,int Patch,int Date,int Build)? ParseVersion(string version){
        var match=Regex.Match(version,@"^v?(\d+)\.(\d+)\.(\d+)-test\.(\d{8})\.(\d+)$");
        if(!match.Success)return null;
        var numbers=new int[5];for(int i=0;i<5;i++)if(!int.TryParse(match.Groups[i+1].Value,out numbers[i]))return null;
        return(numbers[0],numbers[1],numbers[2],numbers[3],numbers[4]);
    }
    internal static AvailableUpdate? Select(string json,string running){
        var current=ParseVersion(running)??throw new InvalidOperationException("当前版本不支持普通版在线更新");
        using var doc=JsonDocument.Parse(json);var candidates=new List<AvailableUpdate>();
        foreach(var release in doc.RootElement.EnumerateArray()){
            if(release.GetProperty("draft").GetBoolean())continue;
            string version=release.GetProperty("tag_name").GetString()?.TrimStart('v')??"";
            var parsed=ParseVersion(version);if(parsed==null||parsed.Value.CompareTo(current)<=0)continue;
            string expected="灰Desk-"+version+"-安装包.exe";
            foreach(var asset in release.GetProperty("assets").EnumerateArray()){
                if(asset.GetProperty("name").GetString()!=expected)continue;
                string digest=asset.TryGetProperty("digest",out var d)?d.GetString()??"":"";
                if(!Regex.IsMatch(digest,@"^sha256:[a-fA-F0-9]{64}$"))continue;
                string url=asset.GetProperty("browser_download_url").GetString()??"";
                if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="github.com"
                    ||!uri.AbsolutePath.StartsWith("/"+Repository+"/releases/download/",StringComparison.Ordinal))continue;
                long size=asset.GetProperty("size").GetInt64();if(size<=0||size>512L*1024*1024)continue;
                candidates.Add(new(version,expected,uri,size,digest[7..].ToUpperInvariant()));
            }
        }
        return candidates.OrderByDescending(c=>ParseVersion(c.Version)!.Value).FirstOrDefault();
    }
    private static HttpClient Client(){
        var client=new HttpClient{Timeout=TimeSpan.FromMinutes(8)};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GrayDesk-Updater/2.2");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");return client;
    }
    internal static async Task<AvailableUpdate?> CheckAsync(string running,CancellationToken token){
        using var client=Client();using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(20));
        string config=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GrayDesk20","update-source.json");
        string endpoint=DefaultManifest;
        if(File.Exists(config)){
            using var source=JsonDocument.Parse(File.ReadAllText(config));
            endpoint=source.RootElement.GetProperty("ManifestUrl").GetString()??"";
        }
        if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)||!AllowedTransport(uri))throw new InvalidDataException("更新源仅允许指定HTTP主机或HTTPS地址");
        return SelectManifest(await client.GetStringAsync(uri,timeout.Token),running,uri);
    }
    internal static AvailableUpdate? SelectManifest(string json,string running,Uri source){
        if(!AllowedTransport(source))throw new InvalidDataException("更新源地址不受信任");
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
        if(root.GetProperty("product").GetString()!="graydesk"||root.GetProperty("edition").GetString()!="standard"||root.GetProperty("arch").GetString()!="win-x64")throw new InvalidDataException("更新清单产品不匹配");
        string version=root.GetProperty("version").GetString()??"";
        var parsed=ParseVersion(version);var current=ParseVersion(running);
        if(parsed==null||current==null)throw new InvalidDataException("更新版本格式错误");
        if(parsed.Value.CompareTo(current.Value)<=0)return null;
        string name=root.GetProperty("fileName").GetString()??"",hash=root.GetProperty("sha256").GetString()??"";
        long size=root.GetProperty("size").GetInt64();
        if(name!="灰Desk-"+version+"-安装包.exe"||!Regex.IsMatch(hash,"^[a-fA-F0-9]{64}$")||size<=0||size>512L*1024*1024)throw new InvalidDataException("更新清单文件校验字段错误");
        if(!Uri.TryCreate(root.GetProperty("downloadUrl").GetString(),UriKind.Absolute,out var download)||!AllowedTransport(download)||!(download.Host.Equals(source.Host,StringComparison.OrdinalIgnoreCase)||download.Scheme=="https"&&download.Host=="github.com"&&download.AbsolutePath.StartsWith("/"+Repository+"/releases/download/",StringComparison.Ordinal)))throw new InvalidDataException("更新下载地址不受信任");
        return new(version,name,download,size,hash.ToUpperInvariant());
    }
    internal static async Task DownloadAsync(HttpClient client,AvailableUpdate update,string destination,IProgress<int>? progress,CancellationToken token){
        string partial=destination+".partial";
        try{
            using var response=await client.GetAsync(update.Download,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
            await using(var input=await response.Content.ReadAsStreamAsync(token))
            await using(var file=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true)){
                var buffer=new byte[81920];long read=0;int count;
                while((count=await input.ReadAsync(buffer,token))>0){read+=count;if(read>update.Size)throw new InvalidDataException("更新包大小不符，已停止");await file.WriteAsync(buffer.AsMemory(0,count),token);progress?.Report((int)(read*100/update.Size));}
                if(read!=update.Size)throw new InvalidDataException("更新包下载不完整，已停止");
            }
            await using(var file=File.OpenRead(partial)){
                string hash=Convert.ToHexString(await SHA256.HashDataAsync(file,token));
                if(!hash.Equals(update.Hash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("更新包校验失败，未运行安装程序");
            }
            token.ThrowIfCancellationRequested();File.Move(partial,destination);
        }finally{if(File.Exists(partial))File.Delete(partial);}
    }
    internal static async Task ShowAsync(System.Windows.Window owner,string running,Func<CancellationToken,Task<AvailableUpdate?>>? check=null){
        var cancellation=new CancellationTokenSource();
        var status=new System.Windows.Controls.TextBlock{Text="正在检查新版本…",TextWrapping=System.Windows.TextWrapping.Wrap,Margin=new System.Windows.Thickness(18)};
        var cancel=new System.Windows.Controls.Button{Content="取消",Height=30,Width=80,Margin=new System.Windows.Thickness(12),HorizontalAlignment=System.Windows.HorizontalAlignment.Right};
        var panel=new System.Windows.Controls.DockPanel();System.Windows.Controls.DockPanel.SetDock(cancel,System.Windows.Controls.Dock.Bottom);panel.Children.Add(cancel);panel.Children.Add(status);
        var window=new System.Windows.Window{Title="灰Desk · 在线更新",Width=390,Height=200,ResizeMode=System.Windows.ResizeMode.NoResize,Topmost=true,Content=panel,WindowStartupLocation=System.Windows.WindowStartupLocation.CenterScreen};
        bool closed=false;window.Closed+=(_,_)=>{closed=true;cancellation.Cancel();cancellation.Dispose();};cancel.Click+=(_,_)=>window.Close();window.Show();
        try{
            var update=await (check?.Invoke(cancellation.Token)??CheckAsync(running,cancellation.Token));if(closed)return;
            if(update==null){status.Text="当前版本："+running+"\n没有可用的更新版本。";cancel.Content="关闭";return;}
            status.Text="发现普通版 "+update.Version+"，约 "+Math.Ceiling(update.Size/1048576d)+" MB。";
            if(System.Windows.MessageBox.Show(window,"下载并打开新版安装包？安装时会退出并卸载旧普通版，保留设置。","灰Desk在线更新",System.Windows.MessageBoxButton.YesNo,System.Windows.MessageBoxImage.Question)!=System.Windows.MessageBoxResult.Yes){window.Close();return;}
            string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GrayDesk20","Updates",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
            string path=Path.Combine(folder,update.Name);using var client=Client();
            await DownloadAsync(client,update,path,new Progress<int>(percent=>{if(!closed)status.Text="正在下载 "+update.Version+"："+percent+"%";}),cancellation.Token);
            if(closed)return;
            // The installer performs exit/uninstall only after the user clicks Install.
            Process.Start(new ProcessStartInfo(path){UseShellExecute=true});window.Close();
        }catch(OperationCanceledException){if(!closed){status.Text="检查或下载超时，请稍后重试。";cancel.Content="关闭";}}
        catch(HttpRequestException ex) when(ex.StatusCode==System.Net.HttpStatusCode.NotFound){if(!closed){status.Text="更新源暂未上架安装包，请稍后再检查。\n当前版本仍可继续使用。";cancel.Content="关闭";}}
        catch(Exception ex){Log.Write("在线更新未完成 "+ex.GetType().Name);if(!closed){status.Text="更新未完成："+(ex is InvalidDataException?ex.Message:"无法取得或下载更新，请检查网络后重试。")+"\n当前版本仍可继续使用。";cancel.Content="关闭";}}
    }
}
