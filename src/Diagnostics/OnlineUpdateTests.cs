using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteConnectHelper.V2.Infrastructure;

namespace RemoteConnectHelper.V2.Diagnostics;
internal static class OnlineUpdateTests
{
    internal static async Task<int> RunAsync(string output){
        Directory.CreateDirectory(output);var rows=new List<object>();bool passed=false;
        void Check(bool ok,string name){rows.Add(new{Name=name,Passed=ok});if(!ok)throw new InvalidOperationException(name);}
        byte[] payload="verified ordinary installer fixture"u8.ToArray();string hash=Convert.ToHexString(SHA256.HashData(payload));
        object Release(string v,string? digest=null,string? name=null,string? url=null,bool draft=false)=>new{draft,tag_name="v"+v,assets=new[]{new{name=name??"灰Desk-"+v+"-安装包.exe",digest=digest??"sha256:"+hash,size=payload.Length,browser_download_url=url??"https://github.com/"+OnlineUpdater.Repository+"/releases/download/v"+v+"/fixture.exe"}}};
        try{
            var chosen=OnlineUpdater.Select(JsonSerializer.Serialize(new[]{Release("2.2.0-test.20261027.21"),Release("2.2.0-test.20261027.24"),Release("2.2.0-test.20261027.23")}),"2.2.0-test.20261027.23");
            Check(chosen?.Version=="2.2.0-test.20261027.24","Selects highest newer ordinary version");
            Check(OnlineUpdater.Select(JsonSerializer.Serialize(new[]{Release("2.2.0-test.20261027.21")}),"2.2.0-test.20261027.23")==null,"Never downgrades to older release");
            Check(OnlineUpdater.Select(JsonSerializer.Serialize(new[]{Release("2.2.0-test.20261027.24-hecheng")}),"2.2.0-test.20261027.23")==null,"Customized release cannot replace ordinary edition");
            Check(OnlineUpdater.Select(JsonSerializer.Serialize(new[]{Release("2.2.0-test.20261027.24",draft:true)}),"2.2.0-test.20261027.23")==null,"Draft release is excluded");
            Check(OnlineUpdater.Select(JsonSerializer.Serialize(new[]{Release("2.2.0-test.20261027.24",digest:"")}),"2.2.0-test.20261027.23")==null,"Missing SHA256 blocks executable selection");
            Check(OnlineUpdater.Select(JsonSerializer.Serialize(new[]{Release("2.2.0-test.20261027.24",name:"theme.exe")}),"2.2.0-test.20261027.23")==null,"Unexpected installer name is rejected");
            Check(OnlineUpdater.Select(JsonSerializer.Serialize(new[]{Release("2.2.0-test.20261027.24",url:"https://example.com/bad.exe")}),"2.2.0-test.20261027.23")==null,"Other download hosts are rejected");
            string manifest=JsonSerializer.Serialize(new{product="graydesk",edition="standard",arch="win-x64",version="2.2.0-test.20261027.24",fileName="灰Desk-2.2.0-test.20261027.24-安装包.exe",sha256=hash,size=payload.Length,downloadUrl="https://updates.example.com/packages/fixture.exe"});
            Check(OnlineUpdater.SelectManifest(manifest,"2.2.0-test.20261027.23",new Uri("https://updates.example.com/manifest"))?.Version=="2.2.0-test.20261027.24","Xiaohuiji manifest contract selects matching ordinary edition");
            bool badManifest=false;
            try{OnlineUpdater.SelectManifest(manifest.Replace("standard","hecheng"),"2.2.0-test.20261027.23",new Uri("https://updates.example.com/manifest"));}catch(InvalidDataException){badManifest=true;}
            Check(badManifest,"Custom edition in external manifest is blocked");
            badManifest=false;
            try{OnlineUpdater.SelectManifest(manifest.Replace("https://updates.example.com/packages/fixture.exe","http://updates.example.com/fixture.exe"),"2.2.0-test.20261027.23",new Uri("https://updates.example.com/manifest"));}catch(InvalidDataException){badManifest=true;}
            Check(badManifest,"Non-HTTPS external installer is rejected");
            var httpSource=new Uri(OnlineUpdater.DefaultManifest);
            string httpManifest=manifest.Replace("https://updates.example.com/packages/fixture.exe","http://"+OnlineUpdater.HttpHost+"/packages/fixture.exe");
            Check(OnlineUpdater.AllowedTransport(httpSource)&&OnlineUpdater.SelectManifest(httpManifest,"2.2.0-test.20261027.23",httpSource)?.Download.Scheme=="http","Exact approved HTTP host permits manifest and same-host download");
            foreach(string address in new[]{"http://updates.example.com/manifest","http://"+OnlineUpdater.HttpHost+".example.com/manifest","http://"+OnlineUpdater.HttpHost+":8080/manifest","http://user@"+OnlineUpdater.HttpHost+"/manifest"})
                Check(!OnlineUpdater.AllowedTransport(new Uri(address)),"HTTP exception rejects unapproved address: "+address);
            badManifest=false;
            try{OnlineUpdater.SelectManifest(httpManifest.Replace("http://"+OnlineUpdater.HttpHost+"/packages/fixture.exe","http://other.example.com/fixture.exe"),"2.2.0-test.20261027.23",httpSource);}catch(InvalidDataException){badManifest=true;}
            Check(badManifest,"Approved HTTP manifest cannot choose another HTTP download host");
            badManifest=false;
            try{OnlineUpdater.SelectManifest(httpManifest.Replace("standard","hecheng"),"2.2.0-test.20261027.23",httpSource);}catch(InvalidDataException){badManifest=true;}
            Check(badManifest,"HTTP exception preserves edition isolation");
            Check(OnlineUpdater.SelectManifest(httpManifest,"2.2.0-test.20261028.1",httpSource)==null,"HTTP exception preserves downgrade prevention");
            using var client=new HttpClient(new PayloadHandler(payload));
            var good=chosen!;string file=Path.Combine(output,"verified.exe");
            await OnlineUpdater.DownloadAsync(client,good,file,null,CancellationToken.None);
            Check(File.ReadAllBytes(file).SequenceEqual(payload)&&!File.Exists(file+".partial"),"Verified complete download becomes final file");
            var approvedHttp=OnlineUpdater.SelectManifest(httpManifest,"2.2.0-test.20261027.23",httpSource)!;
            string httpFile=Path.Combine(output,"approved-http.exe");await OnlineUpdater.DownloadAsync(client,approvedHttp,httpFile,null,CancellationToken.None);
            Check(File.ReadAllBytes(httpFile).SequenceEqual(payload),"Approved HTTP download still passes size and SHA256 verification");
            string bad=Path.Combine(output,"bad.exe");bool rejected=false;
            try{await OnlineUpdater.DownloadAsync(client,good with{Hash=new string('0',64)},bad,null,CancellationToken.None);}catch(InvalidDataException){rejected=true;}
            Check(rejected&&!File.Exists(bad)&&!File.Exists(bad+".partial"),"Hash mismatch removes partial file without running installer");
            string shortFile=Path.Combine(output,"incomplete.exe");rejected=false;
            try{await OnlineUpdater.DownloadAsync(client,good with{Size=good.Size+1},shortFile,null,CancellationToken.None);}catch(InvalidDataException){rejected=true;}
            Check(rejected&&!File.Exists(shortFile)&&!File.Exists(shortFile+".partial"),"Incomplete download cannot become executable");
            using var cancellation=new CancellationTokenSource();cancellation.Cancel();rejected=false;
            try{await OnlineUpdater.DownloadAsync(client,good,Path.Combine(output,"cancel.exe"),null,cancellation.Token);}catch(OperationCanceledException){rejected=true;}
            Check(rejected&&!File.Exists(Path.Combine(output,"cancel.exe.partial")),"Cancellation leaves no partial installer");
            passed=true;
        }catch(Exception ex){rows.Add(new{Name="Fixture",Passed=false,Error=ex.Message});}
        File.WriteAllText(Path.Combine(output,"在线更新验收.json"),JsonSerializer.Serialize(new{Passed=passed,Scope="Release-selection and in-memory HTTPS download fixtures; no real install or release publishing",Tests=rows},new JsonSerializerOptions{WriteIndented=true}));return passed?0:1;
    }
    private sealed class PayloadHandler(byte[] payload):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(payload)});}}
}
