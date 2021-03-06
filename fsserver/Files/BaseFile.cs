﻿using System;
using System.IO;
using NMaier.SimpleDlna.FileMediaServer.Folders;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Server.Metadata;
using NMaier.SimpleDlna.Utilities;

namespace NMaier.SimpleDlna.FileMediaServer.Files
{
  internal class BaseFile : Logging, IMediaResource, IFileServerMediaItem, IMediaCover, IMetaInfo
  {

    private WeakReference _cover = new WeakReference(null);
    private static readonly LruDictionary<string, Cover> coverCache = new LruDictionary<string, Cover>(500);
    private DateTime? lastModified = null;
    private long? length = null;
    private readonly string title;
    private readonly FileServer server;



    protected BaseFile(FileServer server, FileInfo aFile, DlnaType aType, MediaTypes aMediaType)
    {
      if (server == null) {
        throw new ArgumentNullException("server");
      }
      this.server = server;
      Item = aFile;

      length = Item.Length;
      lastModified = Item.LastWriteTimeUtc;

      Type = aType;
      MediaType = aMediaType;

      title = System.IO.Path.GetFileNameWithoutExtension(Item.Name);
      if (string.IsNullOrEmpty(title)) {
        title = Item.Name;
      }
      if (!string.IsNullOrWhiteSpace(title)) {
        title = Uri.UnescapeDataString(title);
      }
      if (!title.Contains(" ")) {
        foreach (var c in new char[] { '_', '+', '.' }) {
          title = title.Replace(c, ' ');
        }
      }
    }



    public Stream Content
    {
      get
      {
        return new FileStream(
          Item.FullName,
          FileMode.Open,
          FileAccess.Read,
          FileShare.ReadWrite | FileShare.Delete,
          1<<17,
          FileOptions.Asynchronous | FileOptions.SequentialScan
          );
      }
    }

    protected Cover cover
    {
      get { return _cover.Target as Cover; }
      set
      {
        if (value != null) {
          coverCache[Item.FullName] = value;
        }
        _cover = new WeakReference(value);
      }
    }

    public virtual IMediaCoverResource Cover
    {
      get
      {
        if (cover == null && !LoadCoverFromCache()) {
          cover = new Cover(Item);
          cover.OnCoverLazyLoaded += LazyLoadedCover;
        }
        return cover;
      }
    }

    public string Id
    {
      get;
      set;
    }

    public DateTime InfoDate
    {
      get
      {
        if (lastModified == null) {
          lastModified = Item.LastWriteTimeUtc;
        }
        return lastModified.Value;
      }
    }

    public long? InfoSize
    {
      get
      {
        if (length == null) {
          length = Item.Length;
        }
        return length;
      }
    }

    internal FileInfo Item
    {
      get;
      set;
    }

    public MediaTypes MediaType
    {
      get;
      protected set;
    }

    public string Path
    {
      get { return Item.FullName; }
    }

    public string PN
    {
      get { return DlnaMaps.PN[Type]; }
    }

    public virtual IHeaders Properties
    {
      get
      {
        var rv = new RawHeaders();
        rv.Add("Title", Title);
        rv.Add("MediaType", MediaType.ToString());
        rv.Add("Type", Type.ToString());
        rv.Add("SizeRaw", InfoSize.ToString());
        rv.Add("Size", Utilities.Formatting.FormatFileSize(InfoSize.Value));
        rv.Add("Date", InfoDate.ToString());
        rv.Add("DateO", InfoDate.ToString("o"));
        try {
          if (Cover != null) {
            rv.Add("HasCover", "true");
          }
        }
        catch (Exception) { }
        return rv;
      }
    }

    protected FileServer Server { get { return server; } }
    public virtual string Title
    {
      get { return title; }
    }

    public DlnaType Type
    {
      get;
      protected set;
    }




    public virtual int CompareTo(IMediaItem other)
    {
      return Title.ToLower().CompareTo(other.Title.ToLower());
    }

    public void LazyLoadedCover(object sender, EventArgs e)
    {
      Server.UpdateFileCache(this);
    }

    public void LoadCover()
    {
      if (cover != null) {
        return;
      }
      cover = new Cover(Item);
      cover.OnCoverLazyLoaded += LazyLoadedCover;
      cover.ForceLoad();
      cover = null;
    }

    protected bool LoadCoverFromCache()
    {
      cover = Server.GetCover(this);
      return cover != null;
    }

    internal static BaseFile GetFile(BaseFolder aParentFolder, FileInfo aFile, DlnaType aType, MediaTypes aMediaType)
    {
      switch (aMediaType) {
        case MediaTypes.VIDEO:
          return new VideoFile(aParentFolder.Server, aFile, aType);
        case MediaTypes.AUDIO:
          return new AudioFile(aParentFolder.Server, aFile, aType);
        case MediaTypes.IMAGE:
          return new ImageFile(aParentFolder.Server, aFile, aType);
        default:
          return new BaseFile(aParentFolder.Server, aFile, aType, aMediaType);
      }
    }

    internal Cover MaybeGetCover()
    {
      return cover;
    }
  }
}
