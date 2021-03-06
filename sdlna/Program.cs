﻿using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Layout;
using NMaier.GetOptNet;
using NMaier.SimpleDlna.FileMediaServer;
using NMaier.SimpleDlna.Server;

namespace NMaier.SimpleDlna
{
  public static class Program
  {

    private static ManualResetEvent BlockEvent = new ManualResetEvent(false);
    private static uint CancelHitCount = 0;




    private static void CancelKeyPressed(object sender, ConsoleCancelEventArgs e)
    {
      if (CancelHitCount++ == 3) {
        LogManager.GetLogger(typeof(Program)).Fatal("Emergency exit commencing");
        return;
      }
      e.Cancel = true;
      BlockEvent.Set();
      LogManager.GetLogger(typeof(Program)).Info("Shutdown requested");
      Console.Title = "simple DLNA - shutting down ...";
    }

    private static void ListOrders()
    {
      var items = from v in ComparerRepository.ListItems()
                  orderby v.Key
                  select v;
      Console.WriteLine("Available orders:");
      Console.WriteLine("----------------");
      Console.WriteLine();
      foreach (var i in items) {
        Console.WriteLine("  - " + i.Key);
        Console.WriteLine(i.Value);
        Console.WriteLine();
      }
    }

    private static void ListViews()
    {
      var items = from v in ViewRepository.ListItems()
                  orderby v.Key
                  select v;
      Console.WriteLine("Available views:");
      Console.WriteLine("----------------");
      Console.WriteLine();
      foreach (var i in items) {
        Console.WriteLine("  - " + i.Key);
        Console.WriteLine(i.Value);
        Console.WriteLine();
      }
    }

    static void Main(string[] args)
    {
      Console.WriteLine();
      var options = new Options();
      try {
        Console.TreatControlCAsInput = false;
        Console.CancelKeyPress += CancelKeyPressed;

        options.Parse(args);
        if (options.ShowHelp) {
          options.PrintUsage();
          return;
        }
        if (options.ShowVersion) {
          ShowVersion();
          return;
        }
        if (options.ShowLicense) {
          ShowLicense();
          return;
        }
        if (options.ListViews) {
          ListViews();
          return;
        }
        if (options.ListOrders) {
          ListOrders();
          return;
        }
        if (options.Directories.Length == 0) {
          throw new GetOptException("No directories specified");
        }

        options.SetupLogging();

        HttpServer server = new HttpServer(options.Port);
        try {
          server.Info("CTRL-C to terminate");

          Console.Title = "simple DLNA - starting ...";

          MediaTypes types = options.Types[0];
          foreach (var t in options.Types) {
            types = types | t;
            server.InfoFormat("Enabled type {0}", t);
          }

          var friendlyName = "sdlna";

          if (options.Seperate) {
            foreach (var d in options.Directories) {
              server.InfoFormat("Mounting FileServer for {0}", d.FullName);
              var fs = SetupFileServer(options, types, new DirectoryInfo[] { d });
              friendlyName = fs.FriendlyName;
              server.RegisterMediaServer(fs);
              server.InfoFormat("{0} mounted", d.FullName);
            }
          }
          else {
            server.InfoFormat("Mounting FileServer for {0} ({1})", options.Directories[0], options.Directories.Length);
            var fs = SetupFileServer(options, types, options.Directories);
            friendlyName = fs.FriendlyName;
            server.RegisterMediaServer(fs);
            server.InfoFormat("{0} ({1}) mounted", options.Directories[0], options.Directories.Length);
          }

          Console.Title = String.Format("{0} - running ...", friendlyName);

          Run(server);
        }
        finally {
          server.Dispose();
        }
      }
      catch (GetOptException ex) {
        Console.Error.WriteLine("Error: {0}\n\n", ex.Message);
        options.PrintUsage();
      }
#if DEBUG
      catch (Exception ex) {
        LogManager.GetLogger(typeof(Program)).Fatal("Failed to run", ex);
      }
#endif
    }

    private static void Run(HttpServer server)
    {
      // Basically the main loop, except we don't loop here at all ;)
      BlockEvent.WaitOne();

      server.Info("Going down!");
      server.Info("Closed!");
    }

    private static FileServer SetupFileServer(Options options, MediaTypes types, DirectoryInfo[] d)
    {
      var fs = new FileServer(types, d);
      try {
        foreach (var v in options.Views) {
          try {
            fs.AddView(v);
          }
          catch (RepositoryLookupException) {
            throw new GetOptException("Invalid view " + v);
          }
        }

        if (options.Order != null) {
          try {

            fs.SetOrder(options.Order);
          }
          catch (RepositoryLookupException) {
            throw new GetOptException("Invalid order" + options.Order);
          }
        }
        fs.DescendingOrder = options.DescendingOrder;

        if (options.CacheFile != null) {
          fs.SetCacheFile(options.CacheFile);
        }

        fs.Load();
      }
      catch (Exception) {
        fs.Dispose();
        throw;
      }
      return fs;
    }

    private static void ShowLicense()
    {
      Console.Write(Encoding.UTF8.GetString(Properties.Resources.LICENSE));
    }

    private static void ShowVersion()
    {
      var a = Assembly.GetEntryAssembly();
      var v = a.GetName().Version;
      Console.WriteLine("App:    {0} {1}.{2}.{3}", a.GetName().Name, v.Major, v.Minor, v.Revision);
      a = Assembly.GetAssembly(typeof(FileServer));
      v = a.GetName().Version;
      Console.WriteLine("Files:  {0} {1}.{2}.{3}", a.GetName().Name, v.Major, v.Minor, v.Revision);
      a = Assembly.GetAssembly(typeof(HttpServer));
      v = a.GetName().Version;
      Console.WriteLine("Server: {0} {1}.{2}.{3}", a.GetName().Name, v.Major, v.Minor, v.Revision);
      a = Assembly.GetAssembly(typeof(Utilities.AttributeCollection));
      v = a.GetName().Version;
      Console.WriteLine("Util:   {0} {1}.{2}.{3}", a.GetName().Name, v.Major, v.Minor, v.Revision);

      Console.WriteLine("Http:   {0}", HttpServer.Signature);
    }




    [GetOptOptions(AcceptPrefixType = ArgumentPrefixType.Dashes)]
    private class Options : GetOpt
    {
      [Argument("cache", Helpvar = "file", Helptext = "Cache file to use for storing meta data (default: none)")]
      [ShortArgument('c')]
      public FileInfo CacheFile = null;
      [Argument("sort-descending", Helptext = "Sort order; see --list-sort-orders")]
      [ShortArgument('d')]
      [FlagArgument(true)]
      public bool DescendingOrder = false;
      [Parameters(HelpVar = "Directory")]
      public DirectoryInfo[] Directories = new DirectoryInfo[] { new DirectoryInfo(".") };
      [Argument("list-sort-orders", Helptext = "List all available sort orders")]
      [FlagArgument(true)]
      public bool ListOrders = false;
      [Argument("list-views", Helptext = "List all available views")]
      [FlagArgument(true)]
      public bool ListViews = false;
      [Argument("log-file", Helptext = "Log to specified file as well (default: none)", Helpvar = "File")]
      public FileInfo LogFile = null;
      [Argument("log-level", Helptext = "Log level of OFF, DEBUG, INFO, WARN, ERROR, FATAL (default: INFO)", Helpvar = "level")]
      [ShortArgument('l')]
      public string LogLevel = "INFO";
      [Argument("sort", Helptext = "Sort order; see --list-sort-orders", Helpvar = "order")]
      [ShortArgument('s')]
      public string Order = null;
      private int port = 0;
      [Argument("help", Helptext = "Print usage")]
      [ShortArgument('?')]
      [ShortArgumentAlias('h')]
      [FlagArgument(true)]
      public bool ShowHelp = false;
      [Argument("version", Helptext = "Print version")]
      [ShortArgument('V')]
      [FlagArgument(true)]
      public bool ShowVersion = false;
      [Argument("license", Helptext = "Print license")]
      [ShortArgument('L')]
      [FlagArgument(true)]
      public bool ShowLicense = false;
      [Argument("type", Helptext = "Types to serv (IMAGE, VIDEO, AUDIO; default: all)")]
      [ArgumentAlias("what")]
      [ShortArgument('t')]
      public MediaTypes[] Types = new MediaTypes[] { MediaTypes.VIDEO, MediaTypes.IMAGE, MediaTypes.AUDIO };
      [Argument("view", Helptext = "Apply a view (default: no views applied)", Helpvar = "view")]
      [ShortArgument('v')]
      public string[] Views = new string[0];
      [Argument("seperate", Helptext = "Mount directories as seperate servers")]
      [ShortArgument('m')]
      [FlagArgument(true)]
      public bool Seperate = false;

      [Argument("port", Helpvar = "port", Helptext = "Webserver listen port (default: 0, bind an available port)")]
      [ShortArgument('p')]
      public int Port
      {
        get { return port; }
        set
        {
          if (value != 0 && (value < 1 || value > ushort.MaxValue)) {
            throw new GetOptException("Port must be between 2 and " + ushort.MaxValue.ToString());
          }
          port = value;
        }
      }

      public void SetupLogging()
      {
        var appender = new ConsoleAppender();
        var layout = new PatternLayout();
        layout.ConversionPattern = "%6level [%3thread] %-14.14logger{1} - %message%newline%exception";
        layout.ActivateOptions();
        appender.Layout = layout;
        appender.ActivateOptions();
        if (LogFile != null) {
          var fileAppender = new RollingFileAppender();
          fileAppender.File = LogFile.FullName;
          fileAppender.Layout = layout;
          fileAppender.MaximumFileSize = "1MB";
          fileAppender.MaxSizeRollBackups = 10;
          fileAppender.RollingStyle = RollingFileAppender.RollingMode.Size;
          fileAppender.ActivateOptions();
          BasicConfigurator.Configure(appender, fileAppender);
        }
        else {
          BasicConfigurator.Configure(appender);
        }

        var repo = LogManager.GetRepository();
        var level = repo.LevelMap[LogLevel.ToUpper()];
        if (level == null) {
          throw new GetOptException("Invalid log level");
        }
        repo.Threshold = level;
      }
    }
  }
}
