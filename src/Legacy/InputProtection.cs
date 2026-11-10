using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace RemoteConnectHelper.Automation;

/// <summary>Protect only a focused fill operation. No key contents are stored or logged.</summary>
internal sealed class InputProtection : IDisposable
{
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SetWindowsHookExW(int type, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window,uint command);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    private readonly ConnectionContext _context;
    private readonly CancellationToken _token;
    private readonly IntPtr _target;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly HookProc _keyboard, _mouse;
    private IntPtr _keyboardHook, _mouseHook;
    private Exception? _error;
    private int _ended, _cancelled;
    private int _armed;
    private IntPtr _returnTarget;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    internal const int MaximumMilliseconds = 6000;
    internal static string LastMouseEvent="none";
    private static int _placingCursor,_cursorX,_cursorY;
    internal static void PlaceCursor(int x,int y,Action placement)
    {
        _cursorX=x;_cursorY=y;Volatile.Write(ref _placingCursor,1);
        try{placement();}finally{Volatile.Write(ref _placingCursor,0);}
    }
    internal static bool Injected(bool keyboard, uint flags) => (flags & (keyboard ? 0x10u : 1u)) != 0;
    internal static bool OwnInput(bool keyboard,uint flags,UIntPtr extra)=>Injected(keyboard,flags)&&extra==InputSimulator.Signature;
    internal static bool Emergency(uint key) => key is 0x1B or 0x20;
    internal bool Released => !_thread.IsAlive;
    internal InputProtection(ConnectionContext context, IntPtr target,bool preparing=false)
    {
        _context=context;_token=context.Token;_target=target;
        _armed=preparing?0:1;
        _keyboard=(code,message,data)=>Filter(true,code,message,data);
        _mouse=(code,message,data)=>Filter(false,code,message,data);
        _thread=new Thread(Run){IsBackground=true,Name="GrayDesk temporary input protection"};
        _thread.SetApartmentState(ApartmentState.STA);_thread.Start();
        if(!_ready.Wait(1000)){Dispose();throw new InvalidOperationException("输入防干扰未能及时启用，已停止填码");}
        if(_error!=null){Dispose();throw new InvalidOperationException("输入防干扰未能启用，已停止填码",_error);}
        try{
            var wait=Stopwatch.StartNew();int[] modifiers=[0x10,0x11,0x12,0x5B,0x5C];
            while(modifiers.Any(key=>(GetAsyncKeyState(key)&0x8000)!=0)&&wait.ElapsedMilliseconds<500){Check();Thread.Sleep(10);}
            if(modifiers.Any(key=>(GetAsyncKeyState(key)&0x8000)!=0))throw new InvalidOperationException("请先松开 Ctrl、Shift、Alt 或 Windows 键，再重试填码");
            Check();
        }catch{Dispose();throw;}
    }
    public static InputProtection Begin(ConnectionContext context)
    {
        ScreenOps.RequireForeground(context);
        return new(context,GetForegroundWindow());
    }
    internal static InputProtection BeginForTarget(ConnectionContext context,ScreenOps.Win window)
    {
        // Close the race between raising the client and installing the hooks.
        var protection=new InputProtection(context,window.Hwnd,preparing:true);
        try{
            if(!ScreenOps.EnsureForeground(window,context))throw new InvalidOperationException("无法置顶目标输入窗口");
            Volatile.Write(ref protection._armed,1);
            protection.Check();return protection;
        }catch{protection.Dispose();throw;}
    }
    internal void AllowReturnToMainWindow()
    {
        var main=_context.MainWindow;
        if(main!=IntPtr.Zero&&main!=_target&&GetWindow(_target,4)==main)_returnTarget=main;
    }
    private bool ForegroundAllowed(){var current=GetForegroundWindow();return current==_target||(_returnTarget!=IntPtr.Zero&&current==_returnTarget);}
    public void Check()
    {
        _context.ThrowIfCancellationRequested();
        if(Volatile.Read(ref _ended)!=0||Volatile.Read(ref _cancelled)!=0||_clock.ElapsedMilliseconds>=MaximumMilliseconds)
            throw new OperationCanceledException("输入防干扰已结束，已停止本次填码");
        if(Volatile.Read(ref _armed)!=0&&!ForegroundAllowed()){Cancel();throw new OperationCanceledException("目标窗口已切换，已停止填码");}
    }
    private void Cancel(){Interlocked.Exchange(ref _cancelled,1);_context.CancelByUser();}
    private IntPtr Filter(bool keyboard,int code,IntPtr message,IntPtr data)
    {
        if(code>=0&&Volatile.Read(ref _ended)==0){
            if(_clock.ElapsedMilliseconds>=MaximumMilliseconds||_token.IsCancellationRequested||(Volatile.Read(ref _armed)!=0&&!ForegroundAllowed())){Cancel();return CallNextHookEx(IntPtr.Zero,code,message,data);}
            uint flags=unchecked((uint)Marshal.ReadInt32(data,keyboard?8:12));
            var extra=unchecked((UIntPtr)(ulong)Marshal.ReadIntPtr(data,keyboard?16:IntPtr.Size==8?24:20).ToInt64());
            if(!keyboard&&message.ToInt64()==0x200)LastMouseEvent=$"flags={flags:X} own={extra==InputSimulator.Signature}";
            // SetCursorPos may be the only working move API in a remote-input session.
            // Permit only this exact pending position update, never a click or key.
            if(!keyboard&&message.ToInt64()==0x200&&Volatile.Read(ref _placingCursor)==1
                &&Marshal.ReadInt32(data)==_cursorX&&Marshal.ReadInt32(data,4)==_cursorY)
                return CallNextHookEx(IntPtr.Zero,code,message,data);
            if(!OwnInput(keyboard,flags,extra)){
                // Releases must reach the desktop so keys held before protection cannot remain stuck.
                if(keyboard&&message.ToInt64() is 0x101 or 0x105)return CallNextHookEx(IntPtr.Zero,code,message,data);
                if(keyboard&&Emergency(unchecked((uint)Marshal.ReadInt32(data))))Cancel();
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(IntPtr.Zero,code,message,data);
    }
    private void Run()
    {
        try{
            using var loop=new Forms.ApplicationContext();
            using var timer=new Forms.Timer{Interval=25};
            var module=GetModuleHandleW(null);
            _keyboardHook=SetWindowsHookExW(13,_keyboard,module,0);
            if(_keyboardHook==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            _mouseHook=SetWindowsHookExW(14,_mouse,module,0);
            if(_mouseHook==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            timer.Tick+=(_,_)=>{
                if(_clock.ElapsedMilliseconds>=MaximumMilliseconds){Cancel();}
                if(Volatile.Read(ref _ended)!=0||Volatile.Read(ref _cancelled)!=0||_token.IsCancellationRequested)loop.ExitThread();
            };
            timer.Start();_ready.Set();Forms.Application.Run(loop);
        }catch(Exception ex){_error=ex;_ready.Set();}
        finally{
            if(_mouseHook!=IntPtr.Zero)UnhookWindowsHookEx(_mouseHook);
            if(_keyboardHook!=IntPtr.Zero)UnhookWindowsHookEx(_keyboardHook);
            Interlocked.Exchange(ref _ended,1);
        }
    }
    public void Dispose(){Interlocked.Exchange(ref _ended,1);if(Thread.CurrentThread!=_thread)_thread.Join(600);}
}
