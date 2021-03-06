using System.Linq;
using NMaier.SimpleDlna.FileMediaServer.Files;
using NMaier.SimpleDlna.FileMediaServer.Folders;
using NMaier.SimpleDlna.Server;

namespace NMaier.SimpleDlna.FileMediaServer.Views
{
  internal sealed class MusicView : IView
  {

    public string Description
    {
      get { return "Reorganizes files into a proper music collection"; }
    }

    public string Name
    {
      get { return "music"; }
    }




    public IMediaFolder Transform(FileServer Server, IMediaFolder Root)
    {
      var root = new VirtualClonedFolder(Root as BaseFolder);
      var artists = new TripleKeyedVirtualFolder(Server, root, "Artists");
      var performers = new TripleKeyedVirtualFolder(Server, root, "Performers");
      var albums = new DoubleKeyedVirtualFolder(Server, root, "Albums");
      var genres = new SimpleKeyedVirtualFolder(Server, root, "Genre");
      var folders = new VirtualFolder(Server, root, "Folders");
      SortFolder(Server, root, artists, performers, albums, genres);
      foreach (var f in root.ChildFolders.ToList()) {
        folders.AdoptFolder(f as BaseFolder);
      }
      root.AdoptFolder(artists);
      root.AdoptFolder(performers);
      root.AdoptFolder(albums);
      root.AdoptFolder(genres);
      root.AdoptFolder(folders);
      return root;
    }

    private static void LinkTriple(TripleKeyedVirtualFolder folder, BaseFile r, string key1, string key2)
    {
      if (string.IsNullOrWhiteSpace(key1)) {
        return;
      }
      if (string.IsNullOrWhiteSpace(key2)) {
        return;
      }
      folder
        .GetFolder(key1.TrimStart().First().ToString().ToUpper())
        .GetFolder(key1)
        .GetFolder(key2)
        .AddFile(r);
    }

    private void SortFolder(FileServer server, BaseFolder folder, TripleKeyedVirtualFolder artists, TripleKeyedVirtualFolder performers, DoubleKeyedVirtualFolder albums, SimpleKeyedVirtualFolder genres)
    {
      foreach (var f in folder.ChildFolders.ToList()) {
        SortFolder(server, f as BaseFolder, artists, performers, albums, genres);
      }
      foreach (var i in folder.ChildItems.ToList()) {
        var ai = i as AudioFile;
        if (ai == null) {
          continue;
        }
        var album = ai.MetaAlbum;
        if (album == null) {
          album = "Unspecified album";
        }
        albums.GetFolder(album.TrimStart().First().ToString().ToUpper()).GetFolder(album).AddFile(ai);
        LinkTriple(artists, ai, ai.MetaArtist, album);
        LinkTriple(performers, ai, ai.MetaPerformer, album);
        var genre = ai.MetaGenre;
        if (genre != null) {
          genres.GetFolder(genre).AddFile(ai);
        }
      }
    }




    private class DoubleKeyedVirtualFolder : KeyedVirtualFolder<SimpleKeyedVirtualFolder>
    {
      public DoubleKeyedVirtualFolder(FileServer server, BaseFolder aParent, string aName)
        : base(server, aParent, aName)
      {
      }

      public DoubleKeyedVirtualFolder() { }
    }
    private class SimpleKeyedVirtualFolder : KeyedVirtualFolder<VirtualFolder>
    {
      public SimpleKeyedVirtualFolder(FileServer server, BaseFolder aParent, string aName)
        : base(server, aParent, aName)
      {
      }

      public SimpleKeyedVirtualFolder() { }
    }
    private class TripleKeyedVirtualFolder : KeyedVirtualFolder<DoubleKeyedVirtualFolder>
    {
      public TripleKeyedVirtualFolder(FileServer server, BaseFolder aParent, string aName)
        : base(server, aParent, aName)
      {
      }

      public TripleKeyedVirtualFolder() { }
    }
  }
}
