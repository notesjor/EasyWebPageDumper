using HtmlAgilityPack;
using System.Net;
using System.Text;

namespace EasyWebPageDumper
{
  internal class Program
  {
    static void Main(string[] args)
    {
      Console.WriteLine("EasyWebPageDumper by J. O. Rüdiger (2023)");
      Console.WriteLine();
      string? url, path;
      if (args.Length == 2)
      {
        url = args[0];
        path = args[1];
      }
      else
        AskInput(out url, out path);

      ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };

      var managerUrl = new UrlManager(url);
      var managerPath = new PathManager(url, path);

      var hdoc = new HtmlDocument();
      var pageEndings = new[] { "/", ".html", ".htm", ".php" };

      using (var wc = new WebClient())
        while (!managerUrl.IsDone)
        {
          url = managerUrl.GetNext();
          if (url == null)
            continue;

          if (pageEndings.Any(x => url.EndsWith(x)))
          {
            hdoc.LoadHtml(wc.DownloadString(url));

            var nodes = hdoc.DocumentNode.SelectNodes("//a[@href]");
            if (nodes != null)
              foreach (var node in nodes)
              {
                var href = node.Attributes["href"].Value;
                href = managerPath.ConvertToAbsolute(href);
                managerUrl.Add(UrlHelper.RemoveAnchors(href));
                node.Attributes["href"].Value = managerPath.ConvertToRelative(href);
              }
            hdoc = DownloadContent(managerPath, hdoc, wc, "//img[@src]", "src");
            hdoc = DownloadContent(managerPath, hdoc, wc, "//script[@src]", "src");
            hdoc = DownloadContent(managerPath, hdoc, wc, "//link[@href]", "href");

            var file = managerPath.ConvertUrlToPath(url);
            if (file.EndsWith("\\"))
              file += "index.html";

            // convert hdoc to html string
            var html = hdoc.DocumentNode.OuterHtml;
            html = html.Replace("<noscript>", "").Replace("</noscript>", "");

            File.WriteAllText(file, html, Encoding.UTF8);
          }
        }
    }

    private static void AskInput(out string? url, out string? path)
    {
      Console.Write("ENTER URL 2 DUMP (with https://): ");
      url = Console.ReadLine();
      Console.Write("ENTER LOCAL PATH: ");
      path = Console.ReadLine();
    }

    private static string[] _removeAttributes = new[] { "data-srcset", "data-src", "srcset" };

    private static HtmlDocument DownloadContent(PathManager managerPath, HtmlDocument hdoc, WebClient wc, string xpath, string attr)
    {
      HtmlNodeCollection? nodes = hdoc.DocumentNode.SelectNodes(xpath);
      if (nodes != null)
        foreach (var node in nodes)
        {
          // remove server side rendering attributes
          foreach (var a in node.GetAttributes().Where(x => _removeAttributes.Contains(x.Name)).Select(x => x.Name).ToArray())
            node.Attributes.Remove(a);

          try
          {
            var src = managerPath.ConvertToAbsolute(node.Attributes[attr].Value);
            if (string.IsNullOrEmpty(src))
              continue;
            if (node.Name == "img" && src.StartsWith("data:"))
            {
              // if img followed by noscript (lazy loading), replace img with noscript img
              // copy attributes from img to noscript img
              // remove lazy loading image 
              // the noscript is removed later

              var next = node.NextSibling;
              while (next == null || next.Name != "noscript")
                continue;

              var imgReplace = next.ChildNodes.FirstOrDefault(x => x.Name == "img");
              if (imgReplace == null)
                continue;

              foreach (var a in node.GetAttributes())
                if (!imgReplace.Attributes.Contains(a.Name))
                  imgReplace.Attributes.Add(a.Name, a.Value);

              node.ParentNode.RemoveChild(node);
              continue;
            }
            if (src.Contains("data:"))
              continue;
            node.Attributes[attr].Value = managerPath.ConvertToRelative(src);
            var file = managerPath.ConvertUrlToPath(src);
            if (!File.Exists(file))
              wc.DownloadFile(src, file);
          }
          catch
          {
            // ignored
          }
        }

      return hdoc;
    }

    public class PathManager
    {
      private string _path;
      private string _url;
      private int _length;

      public PathManager(string url, string path)
      {
        url = url.Trim();
        if (!url.EndsWith("/"))
          url = url + "/";
        _url = url;
        _length = url.Length;

        path = path.Trim();
        _path = path;
      }

      public string ConvertUrlToPath(string url)
      {
        url = UrlHelper.RemoveParameters(url);
        url = this.ConvertToRelative(url);

        var res = _path + url.Replace("/", "\\");

        var dir = Path.GetDirectoryName(res);
        if (!Directory.Exists(dir))
          Directory.CreateDirectory(dir);

        return res;
      }

      public string ConvertToAbsolute(string url)
      {
        if (url.StartsWith("https://") || url.StartsWith("http://"))
          return url;

        url = UrlHelper.RemoveParameters(url);

        if (url.StartsWith("/"))
          return _url + url.Substring(1);

        return _url + url;
      }

      public string ConvertToRelative(string url)
      {
        if (url.StartsWith("/"))
          return url;

        url = UrlHelper.RemoveParameters(url);

        var index = url.IndexOf(_url);
        if (index < 0)
          return url;

        return url.Substring(index + _length - 1);
      }
    }

    private static class UrlHelper
    {
      public static string RemoveParameters(string url)
      {
        var index = url.IndexOf('?');
        return index < 0 ? url : url.Substring(0, index);
      }

      public static string RemoveAnchors(string url)
      {
        var index = url.IndexOf('#');
        return index < 0 ? url : url.Substring(0, index);
      }
    }

    public class UrlManager
    {
      private object _lock = new object();
      private HashSet<string> _done = new HashSet<string>();
      private Queue<string> _todo = new Queue<string>();
      private string _url;

      public UrlManager(string url)
      {
        url = url.Trim();

        if (!url.EndsWith("/"))
          url = url + "/";

        _url = url;
        Add(_url);
      }

      public void Add(string url)
      {
        if (string.IsNullOrEmpty(url))
          return;

        if (!url.StartsWith(_url))
          return;

        url = UrlHelper.RemoveParameters(url);

        lock (_lock)
        {
          if (_done.Contains(url))
            return;
          _todo.Enqueue(url);
        }
      }

      public bool IsDone => _todo.Count == 0;

      public string GetNext()
      {
        lock (_lock)
        {
          while (_todo.Count > 0)
          {
            var url = _todo.Dequeue();
            if (_done.Contains(url))
              continue;
            _done.Add(url);
            return url;
          }
          return null;
        }
      }
    }
  }
}