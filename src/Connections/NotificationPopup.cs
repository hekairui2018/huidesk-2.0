using System.Drawing;
using System.Text.RegularExpressions;
using RemoteConnectHelper.Recognition;
using RemoteConnectHelper.V2.Domain;

namespace RemoteConnectHelper.V2.Connections;

// This optional follow-notification card is not the QR login or authentication page.
internal static class NotificationPopup
{
    private static string Flat(string text)=>Regex.Replace(text,@"\s+","");
    internal static bool Recognized(OcrDocument doc)
    {
        string text=Flat(string.Concat(doc.Blocks.Select(b=>b.Text)));
        return !StartupScreenRecovery.Protected(doc)&&text.Contains("微信扫码关注")&&text.Contains("获取安全通知")
            &&text.Contains("设备异常远程访问")&&text.Contains("近期不再显示");
    }
    internal static StartupAction? Close(Bitmap image,OcrDocument doc)
    {
        if(!Recognized(doc))return null;
        var headers=StartupScreenRecovery.Labels(doc).Where(b=>b.Confidence>=.8f&&Flat(b.Text).Contains("微信扫码关注")).ToArray();
        if(headers.Length!=1)return null;
        var header=headers[0].Layout;
        int height=(int)Math.Round(header.Height);
        if(height<10||height>45)return null;
        var hits=new List<Point>();
        // Locate the actual contrasting X above the recognized card heading. Never click
        // the enclosing application's close button or infer a point from window size.
        int left=Math.Max(0,(int)(header.Right-height*2));
        int right=Math.Min(image.Width-9,(int)(header.Right+height*4));
        int top=Math.Max(9,(int)(header.Y-height*3));
        int bottom=Math.Min(image.Height-9,(int)(header.Y-height*.6));
        for(int y=top;y<=bottom;y++)for(int x=left;x<=right;x++)
        {
            for(int r=Math.Max(3,height/5);r<=Math.Min(9,height/2);r++)
            {
                if(x-r-2<0||x+r+2>=image.Width)continue;
                var background=image.GetPixel(x-r-2,y);
                if(Math.Abs(background.R-background.G)>12||Math.Abs(background.B-background.G)>12)continue;
                bool Bright(int dx,int dy){var c=image.GetPixel(x+dx,y+dy);return c.R-background.R>22&&c.G-background.G>22&&c.B-background.B>22;}
                int diagonal=0,off=0,total=0;
                for(int t=-r;t<=r;t++){if(Bright(t,t))diagonal++;if(Bright(t,-t))diagonal++;}
                for(int dy=-r;dy<=r;dy++)for(int dx=-r;dx<=r;dx++)
                    if(Math.Abs(dx-dy)>2&&Math.Abs(dx+dy)>2){total++;if(Bright(dx,dy))off++;}
                if(diagonal>=(2*r+1)*2*.85&&total>0&&off<=total*.08){hits.Add(new(x,y));break;}
            }
        }
        if(hits.Count==0)return null;
        var center=new Point((int)Math.Round(hits.Average(p=>p.X)),(int)Math.Round(hits.Average(p=>p.Y)));
        if(hits.Any(p=>Math.Abs(p.X-center.X)>6||Math.Abs(p.Y-center.Y)>6))return null;
        return new("关闭微信关注通知",new Box(center.X-3,center.Y-3,6,6));
    }
}
