using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.Recognition;

namespace RemoteConnectHelper.V2.Diagnostics;
internal static class InputProtectionTests
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);var rows=new List<object>();int exit=1;
        var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Dispatcher.BeginInvoke(new Action(async()=>{
            Window? window=null;
            void Check(bool ok,string name){rows.Add(new{Name=name,Passed=ok});if(!ok)throw new InvalidOperationException(name);}
            try{
                Check(InputProtection.Injected(true,0x10)&&InputProtection.Injected(false,1),"Injected keyboard and mouse input passes protection");
                Check(!InputProtection.Injected(true,0)&&!InputProtection.Injected(false,0),"Physical input is distinguished from injected input");
                Check(InputProtection.OwnInput(false,1,InputSimulator.Signature)&&InputProtection.OwnInput(true,0x10,InputSimulator.Signature),"Only GrayDesk-tagged injected input is authorized during protection");
                Check(!InputProtection.OwnInput(false,1,UIntPtr.Zero)&&!InputProtection.OwnInput(true,0x10,UIntPtr.Zero),"Other tools' injected mouse and keyboard input is not authorized");
                Check(InputProtection.Emergency(0x1B)&&InputProtection.Emergency(0x20)&&!InputProtection.Emergency(0x41),"Esc and Space remain emergency stop keys");
                var field=new System.Windows.Controls.TextBox{Text="OLD123",FontSize=28};
                window=new Window{Title="灰Desk自建输入验收",Width=420,Height=140,Topmost=true,Content=field};window.Show();window.Activate();field.Focus();await Task.Delay(120);
                var handle=new WindowInteropHelper(window).Handle;
                InputSimulator.BringToFront(handle);field.Focus();
                window.Topmost=false;
                using(new TargetWindowPriority(handle))Check((RemoteConnectHelper.V2.UI.DesktopWindows.ExtendedStyle(window)&8)!=0,"Client preparation temporarily promotes verified target above pinned panels");
                Check((RemoteConnectHelper.V2.UI.DesktopWindows.ExtendedStyle(window)&8)==0,"Temporary target priority is restored after preparation");
                window.Topmost=true;
                using(new TargetWindowPriority(handle)){}
                Check((RemoteConnectHelper.V2.UI.DesktopWindows.ExtendedStyle(window)&8)!=0,"Existing topmost preference is preserved");
                InputSimulator.BringToFront(handle);field.Focus();
                using var context=new ContextOwner();
                InputProtection protection=null!;
                var target=field.PointToScreen(new System.Windows.Point(24,24));
                await Task.Run(()=>{
                  using(protection=new InputProtection(context.Context,handle)){
                    InputSimulator.Click((int)target.X,(int)target.Y);
                    var cursor=System.Windows.Forms.Cursor.Position;
                    Check(Math.Abs(cursor.X-target.X)<=1&&Math.Abs(cursor.Y-target.Y)<=1,$"Injected cursor movement reaches the field while input protection is active expected={target} actual={cursor}");
                    InputSimulator.ClearFocusedField();InputSimulator.TypeText("123456");protection.Check();
                  }
                });
                await Task.Delay(60);
                Check(protection.Released&&field.Text=="123456",$"Actual hooks allow clear-and-replace in owned field and release after fill released={protection.Released} fixtureText={field.Text}");
                var child=new Window{Owner=window,Title="灰Desk自建错误提示",Width=200,Height=100,Topmost=true};
                try{
                    child.Show();var childHandle=new WindowInteropHelper(child).Handle;InputSimulator.BringToFront(childHandle);
                    context.Context.MainWindow=handle;
                    using var dismissal=new InputProtection(context.Context,childHandle);
                    dismissal.AllowReturnToMainWindow();child.Close();InputSimulator.BringToFront(handle);dismissal.Check();
                    Check(!context.Context.Token.IsCancellationRequested,"Closing a verified owned failure dialog may return focus to its main window without cancelling retry");
                }finally{child.Close();}
                var filter=typeof(InputProtection).GetMethod("Filter",BindingFlags.NonPublic|BindingFlags.Instance)!;
                using var stopped=new ContextOwner();
                using(var emergency=new InputProtection(stopped.Context,handle)){
                    var data=Marshal.AllocHGlobal(32);
                    try{
                        for(int i=0;i<32;i++)Marshal.WriteByte(data,i,0);
                        Marshal.WriteInt32(data,0,0x41);
                        Check((IntPtr)filter.Invoke(emergency,[true,0,new IntPtr(0x100),data])! ==new IntPtr(1),"Physical keyboard event is suppressed during fill");
                        Check((IntPtr)filter.Invoke(emergency,[false,0,new IntPtr(0x201),data])! ==new IntPtr(1),"Physical mouse click is suppressed during fill");
                        Marshal.WriteInt32(data,0,0x1B);
                        Check((IntPtr)filter.Invoke(emergency,[true,0,new IntPtr(0x100),data])! ==new IntPtr(1)&&stopped.Context.UserCancelled,"Emergency key cancels fill without typing into client");
                    }finally{Marshal.FreeHGlobal(data);}
                }
                using var timeout=new ContextOwner();
                using(var expiring=new InputProtection(timeout.Context,handle)){
                    await Task.Delay(InputProtection.MaximumMilliseconds+200);
                    Check(expiring.Released&&timeout.Context.UserCancelled,"Six-second watchdog releases hooks even without Dispose");
                }
                using var failure=new ContextOwner();InputProtection? exceptional=null;
                try{using(exceptional=new InputProtection(failure.Context,handle)){throw new IOException("fixture");}}catch(IOException){}
                Check(exceptional?.Released==true,"Exception releases keyboard and mouse protection");
                exit=0;
            }catch(Exception ex){rows.Add(new{Name="Fixture",Passed=false,Error=ex.Message});}
            finally{window?.Close();File.WriteAllText(Path.Combine(output,"输入防干扰验收.json"),JsonSerializer.Serialize(new{Passed=exit==0,Scope="Actual temporary hooks with owned input window; hardware callback fixtures; no remote clients operated",Tests=rows},new JsonSerializerOptions{WriteIndented=true}));app.Shutdown();}
        }));app.Run();return exit;
    }
    private sealed class ContextOwner:IDisposable
    {public ConnectionContext Context {get;}=new(RemoteAppKind.Sunlogin);public void Dispose()=>Context.Cts.Dispose();}
}
