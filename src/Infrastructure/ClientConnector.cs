using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Connections;
using RemoteConnectHelper.V2.Discovery;
using RemoteConnectHelper.V2.Domain;
using LegacyKind=RemoteConnectHelper.Recognition.RemoteAppKind;
namespace RemoteConnectHelper.V2.Infrastructure;
public sealed class ClientConnector
{
    /// <summary>
    /// 步E：老「OCR 找框 → 点框 → 打字 → 点连接」路径。
    /// 默认关（新通道是向日葵剪贴板接力 + ToDesk 命令行）；某个客户端版本上两条新路都失灵时，
    /// 托盘「连接方式」里一键退回这里，不用重新出包。
    /// </summary>
    public static bool LegacyClickEnabled { get; set; }

    public async Task<ConnectResult> ConnectAsync(RemoteApp app,string code,string password,CancellationToken token)
    {
        if(!ConnectionPolicy.ValidCode(app,code))return ConnectResult.Fail("请填写有效设备码");
        string key=RemoteApps.Id(app);
        var installed=await Task.Run(()=>RemoteSoftwareDiscovery.Find(key),token);
        if(installed==null)return ConnectResult.Fail("没有发现已验证的远程软件，请从托盘「远程软件」补充路径");

        // 步E·新通道：连接发起不再经过 OCR 定位与物理点按，认码/上报/令牌/客户信息全不动。
        if (!LegacyClickEnabled)
        {
            if(app==RemoteApp.Sunlogin)
                return await SunloginRelay.ConnectAsync(code,password,installed.Path,token);
            if(app==RemoteApp.ToDesk)
                return await ConnectToDeskByCommandAsync(app,code,password,installed.Path,token);
        }

        var before=SessionObserver.Windows(app,false);
        var context=new ConnectionContext(app==RemoteApp.Sunlogin?LegacyKind.Sunlogin:LegacyKind.ToDesk){ExecutablePath=installed.Path};
        context.PriorWindows=before;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,timeout.Token);
        using var registration=linked.Token.Register(()=>{if(token.IsCancellationRequested)context.CancelByUser();else context.Cancel();});
        try
        {
            if(app is RemoteApp.AnyDesk or RemoteApp.UU or RemoteApp.RustDesk or RemoteApp.TeamViewer or RemoteApp.UltraViewer){
                var added=await AdditionalClientConnector.ConnectAsync(app,installed.Path,code,password,linked.Token).WaitAsync(linked.Token);
                if(added.Status is ConnectStatus.Failed or ConnectStatus.NeedPassword or ConnectStatus.Cancelled)return added;
                return await SessionObserver.ConfirmAsync(app,code,before,IntPtr.Zero,linked.Token);
            }
            IRemoteConnector connector=app==RemoteApp.Sunlogin?new SunloginConnector():new ToDeskConnector();
            var result=await connector.ConnectAsync(code,password,context).WaitAsync(linked.Token);
            Log.Write("客户端连接阶段结束 app="+app+" status="+result.Status+" clicked="+context.ConnectClicked+" reason="+result.Message);
            token.ThrowIfCancellationRequested();
            if(timeout.IsCancellationRequested)return ConnectResult.Unknown("等待超时，尚未确认连接；请检查远程软件");
            if(result.Status is ConnectStatus.Failed or ConnectStatus.NeedPassword or ConnectStatus.Cancelled)return result;
            return await SessionObserver.ConfirmAsync(app,code,before,context.MainWindow,linked.Token);
        }
        catch(OperationCanceledException)
        {return token.IsCancellationRequested||context.UserCancelled?ConnectResult.Cancel("已停止助手操作；已有会话保持不变"):ConnectResult.Unknown("等待超时，尚未确认连接");}
        catch(InvalidOperationException ex){Log.Write("连接已停止 "+ex.Message);return ConnectResult.Fail(ex.Message);}
        catch(Exception ex){RemoteSoftwareDiscovery.Invalidate(key);Log.Write("连接异常 "+ex.GetType().Name);return ConnectResult.Fail("连接未完成，请检查客户端并重试");}
        finally{context.Cts.Dispose();} // Never WM_CLOSE a window based merely on when it appeared.
    }

    /// <summary>
    /// 步E·ToDesk：命令行发出去，再用会话观察器判「连上/要密码/失败」。
    /// 助手不点任何按钮；命令行没叫起来就直接报失败，交人工，不去乱点。
    /// </summary>
    private static async Task<ConnectResult> ConnectToDeskByCommandAsync(RemoteApp app,string code,string password,string exePath,CancellationToken token)
    {
        var before=SessionObserver.Windows(app,false);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,timeout.Token);
        try
        {
            var launch=await ToDeskDirectConnector.ConnectAsync(exePath,code,password,linked.Token);
            if(launch.Status is ConnectStatus.Failed or ConnectStatus.Cancelled or ConnectStatus.NeedPassword)return launch;
            return await SessionObserver.ConfirmAsync(app,code,before,IntPtr.Zero,linked.Token);
        }
        catch(OperationCanceledException)
        {
            return token.IsCancellationRequested
                ? ConnectResult.Cancel("已停止助手操作；已有会话保持不变")
                : ConnectResult.Unknown("等待超时，尚未确认 ToDesk 连接；请查看客户端提示");
        }
        catch(Exception ex)
        {
            Log.Write("ToDesk 命令行异常 "+ex.GetType().Name+" "+ex.Message);
            return ConnectResult.Fail("ToDesk 连接未完成："+ex.Message);
        }
    }
}
