﻿using System;

namespace NMaier.SimpleDlna.Server
{
  public interface IMediaItem : IComparable<IMediaItem>
  {

    string Id { get; }

    IHeaders Properties { get; }

    string Title { get; }
  }
}