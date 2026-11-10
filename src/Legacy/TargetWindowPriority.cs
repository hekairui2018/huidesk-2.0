using System.Runtime.InteropServices;

namespace RemoteConnectHelper.Automation;

/// <summary>Raise a verified client while preparing input, then restore its original priority.</summary>
internal sealed class TargetWindowPriority : IDisposable
{
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern IntPtr GetWindowLongPtr(IntPtr h,int index);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
    private readonly IntPtr _window;private readonly bool _wasTopmost;
    public TargetWindowPriority(IntPtr window){
        _window=window;_wasTopmost=(GetWindowLongPtr(window,-20).ToInt64()&8)!=0;
        if(!SetWindowPos(window,new IntPtr(-1),0,0,0,0,0x13))throw new InvalidOperationException("未能将远程软件置前，已停止填码");
    }
    public void Dispose(){if(!_wasTopmost)SetWindowPos(_window,new IntPtr(-2),0,0,0,0,0x13);}
}
