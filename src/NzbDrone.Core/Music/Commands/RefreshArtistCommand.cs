﻿using NzbDrone.Core.Messaging.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NzbDrone.Core.Music.Commands
{
    public class RefreshArtistCommand : Command
    {
        public int? ArtistId { get; set; }

        public RefreshArtistCommand()
        {
        }

        public RefreshArtistCommand(int? artistId)
        {
            ArtistId = artistId;
        }

        public override bool SendUpdatesToClient => true;

        public override bool UpdateScheduledTask => !ArtistId.HasValue;
    }
}
