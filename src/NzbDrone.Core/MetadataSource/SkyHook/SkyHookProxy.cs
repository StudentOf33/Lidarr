﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NLog;
using NzbDrone.Common.Cloud;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource.SkyHook.Resource;
using NzbDrone.Core.Tv;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.Music;
using Newtonsoft.Json;

namespace NzbDrone.Core.MetadataSource.SkyHook
{
    public class SkyHookProxy : IProvideSeriesInfo, IProvideArtistInfo, ISearchForNewSeries
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        private readonly IHttpRequestBuilderFactory _requestBuilder;

        public SkyHookProxy(IHttpClient httpClient, ILidarrCloudRequestBuilder requestBuilder, Logger logger)
        {
            _httpClient = httpClient;
             _requestBuilder = requestBuilder.Search;
            _logger = logger;
        }

       

        public Tuple<Series, List<Episode>> GetSeriesInfo(int tvdbSeriesId)
        {
            Console.WriteLine("[GetSeriesInfo] id:" + tvdbSeriesId);
            var httpRequest = _requestBuilder.Create()
                                             .SetSegment("route", "shows")
                                             .Resource(tvdbSeriesId.ToString())
                                             .Build();

            httpRequest.AllowAutoRedirect = true;
            httpRequest.SuppressHttpError = true;

            var httpResponse = _httpClient.Get<ShowResource>(httpRequest);

            if (httpResponse.HasHttpError)
            {
                if (httpResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new SeriesNotFoundException(tvdbSeriesId);
                }
                else
                {
                    throw new HttpException(httpRequest, httpResponse);
                }
            }

            var episodes = httpResponse.Resource.Episodes.Select(MapEpisode);
            var series = MapSeries(httpResponse.Resource);

            return new Tuple<Series, List<Episode>>(series, episodes.ToList());
        }

        public List<Series> SearchForNewSeries(string title)
        {
            // TODO: Remove this API
            var tempList = new List<Series>();
            var tempSeries = new Series();
            tempSeries.Title = "AFI";
            tempList.Add(tempSeries);
            return tempList;
        }


        public Tuple<Artist, List<Track>> GetArtistInfo(string spotifyId)
        {

            _logger.Debug("Getting Artist with SpotifyId of {0}", spotifyId);

            ///v1/albums/{id}
            //

            // We need to perform a direct lookup of the artist
            var httpRequest = _requestBuilder.Create()
                                            .SetSegment("route", "artists/" + spotifyId)
                                             //.SetSegment("route", "search")
                                             //.AddQueryParam("type", "artist,album")
                                             //.AddQueryParam("q", spotifyId.ToString())
                                             .Build();

            

            httpRequest.AllowAutoRedirect = true;
            httpRequest.SuppressHttpError = true;

            var httpResponse = _httpClient.Get<ArtistInfoResource>(httpRequest);
            

            if (httpResponse.HasHttpError)
            {
                if (httpResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new ArtistNotFoundException(spotifyId);
                }
                else
                {
                    throw new HttpException(httpRequest, httpResponse);
                }
            }

            Artist artist = new Artist();
            artist.ArtistName = httpResponse.Resource.Name;
            artist.SpotifyId = httpResponse.Resource.Id;
            artist.Genres = httpResponse.Resource.Genres;

            var albumRet = MapAlbums(artist);
            artist = albumRet.Item1;



            return new Tuple<Artist, List<Track>>(albumRet.Item1, albumRet.Item2);
        }

        private Tuple<Artist, List<Track>> MapAlbums(Artist artist)
        {

            // Find all albums for the artist and all tracks for said album
            ///v1/artists/{id}/albums
            var httpRequest = _requestBuilder.Create()
                                            .SetSegment("route", "artists/" + artist.SpotifyId + "/albums")
                                            .Build();
            httpRequest.AllowAutoRedirect = true;
            httpRequest.SuppressHttpError = true;

            var httpResponse = _httpClient.Get<AlbumResultResource>(httpRequest);

            if (httpResponse.HasHttpError)
            {
                throw new HttpException(httpRequest, httpResponse);
            }

            List<Track> masterTracks = new List<Track>();
            List<Album> albums = new List<Album>();
            foreach(var albumResource in httpResponse.Resource.Items)
            {
                Album album = new Album();
                album.AlbumId = albumResource.Id;
                album.Title = albumResource.Name;
                album.ArtworkUrl = albumResource.Images.Count > 0 ? albumResource.Images[0].Url : "";
                album.Tracks = MapTracksToAlbum(album);
                masterTracks.InsertRange(masterTracks.Count, album.Tracks);
                albums.Add(album);
            }

            // TODO: We now need to get all tracks for each album

            artist.Albums = albums;
            return new Tuple<Artist, List<Track>>(artist, masterTracks);
        }

        private List<Track> MapTracksToAlbum(Album album)
        {
            var httpRequest = _requestBuilder.Create()
                                            .SetSegment("route", "albums/" + album.AlbumId + "/tracks")
                                            .Build();

            httpRequest.AllowAutoRedirect = true;
            httpRequest.SuppressHttpError = true;

            var httpResponse = _httpClient.Get<TrackResultResource>(httpRequest);

            if (httpResponse.HasHttpError)
            {
                throw new HttpException(httpRequest, httpResponse);
            }

            List<Track> tracks = new List<Track>();
            foreach(var trackResource in httpResponse.Resource.Items)
            {
                Track track = new Track();
                track.AlbumId = album.AlbumId;
                //track.Album = album; // This will cause infinite loop when trying to serialize. 
                // TODO: Implement more track mapping
                //track.Artist = trackResource.Artists
                //track.ArtistId = album.
                track.SpotifyTrackId = trackResource.Id;
                track.ArtistSpotifyId = trackResource.Artists.Count > 0 ? trackResource.Artists[0].Id : null;
                track.Explict = trackResource.Explicit;
                track.Compilation = trackResource.Artists.Count > 1;
                track.TrackNumber = trackResource.TrackNumber;
                track.Title = trackResource.Name;
                tracks.Add(track);
            }

            return tracks;
        }


        public List<Artist> SearchForNewArtist(string title)
        {
            // Discogs attempt:
            // First query for artist
            // https://api.discogs.com/database/search?q=taking back sunday&type=artist&token=VHuwcMaSdSaUhgNUuQWHOKlXjYTHjwhIZCJEnvRl
            // Return all artists back to user
            // Allow user to expand the artist they want
            // On Expand, query for albums from artistId
            // https://api.discogs.com/artists/252165/releases?sort=year&token=VHuwcMaSdSaUhgNUuQWHOKlXjYTHjwhIZCJEnvRl
            // Remove all non-valid "release" or albums aka Viynl, Smplr, Promo. Maybe only take master releases. 
            // Then allow a user to add a single album or have a button that loops through all albums and adds. 
            // To lookup all tracks, we can use: https://api.discogs.com/masters/355285 <- id of master release. Likewise, releases/id for a non-mastered album
            try
            {
                var lowerTitle = title.ToLowerInvariant();
                Console.WriteLine("Searching for " + lowerTitle);

                if (lowerTitle.StartsWith("itunes:") || lowerTitle.StartsWith("itunesid:"))
                {
                    var slug = lowerTitle.Split(':')[1].Trim();

                    if (slug.IsNullOrWhiteSpace() || slug.Any(char.IsWhiteSpace))
                    {
                        return new List<Artist>();
                    }

                    try
                    {
                        return new List<Artist> { GetArtistInfo(slug).Item1 };
                    }
                    catch (ArtistNotFoundException)
                    {
                        return new List<Artist>();
                    }
                }

                var httpRequest = _requestBuilder.Create()
                                    .SetSegment("route", "search")
                                    .AddQueryParam("type", "artist,album")
                                    .AddQueryParam("q", title.ToLower().Trim())
                                    .Build();



                var httpResponse = _httpClient.Get<ArtistResource>(httpRequest);


                List<Artist> artists = MapArtists(httpResponse.Resource);

                return artists;
            }
            catch (HttpException)
            {
                throw new SkyHookException("Search for '{0}' failed. Unable to communicate with SkyHook.", title);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, ex.Message);
                throw new SkyHookException("Search for '{0}' failed. Invalid response received from SkyHook.", title);
            }
        }

        private Artist MapArtistInfo(ArtistInfoResource resource)
        {
            // This expects ArtistInfoResource, thus just need to populate one artist
            Artist artist = new Artist();
            //artist.Overview = resource.artistBio;
            //artist.ArtistName = resource.name;
            //foreach(var genre in resource.genreNames)
            //{
            //    artist.Genres.Add(genre);
            //}

            return artist;
        }

        private List<Artist> MapArtists(ArtistResource resource)
        {
            

            List<Artist> artists = new List<Artist>();
            foreach(var artistResource in resource.Artists.Items)
            {
                Artist artist = new Artist();
                artist.ArtistName = artistResource.Name;
                artist.SpotifyId = artistResource.Id;
                artist.Genres = artistResource.Genres;
                artist.ArtistSlug = Parser.Parser.CleanArtistTitle(artist.ArtistName);
                artists.Add(artist);
            }

            // Maybe? Get all the albums for said artist


            return artists;
        }

        //private Album MapAlbum(AlbumResource albumQuery)
        //{
        //    Album album = new Album();

        //    album.AlbumId = albumQuery.CollectionId;
        //    album.Title = albumQuery.CollectionName;
        //    album.Year = albumQuery.ReleaseDate.Year;
        //    album.ArtworkUrl = albumQuery.ArtworkUrl100;
        //    album.Explicitness = albumQuery.CollectionExplicitness;
        //    return album;
        //}

        private static Series MapSeries(ShowResource show)
        {
            var series = new Series();
            series.TvdbId = show.TvdbId;

            if (show.TvRageId.HasValue)
            {
                series.TvRageId = show.TvRageId.Value;
            }

            if (show.TvMazeId.HasValue)
            {
                series.TvMazeId = show.TvMazeId.Value;
            }

            series.ImdbId = show.ImdbId;
            series.Title = show.Title;
            series.CleanTitle = Parser.Parser.CleanSeriesTitle(show.Title);
            series.SortTitle = SeriesTitleNormalizer.Normalize(show.Title, show.TvdbId);

            if (show.FirstAired != null)
            {
                series.FirstAired = DateTime.Parse(show.FirstAired).ToUniversalTime();
                series.Year = series.FirstAired.Value.Year;
            }

            series.Overview = show.Overview;

            if (show.Runtime != null)
            {
                series.Runtime = show.Runtime.Value;
            }

            series.Network = show.Network;

            if (show.TimeOfDay != null)
            {
                series.AirTime = string.Format("{0:00}:{1:00}", show.TimeOfDay.Hours, show.TimeOfDay.Minutes);
            }

            series.TitleSlug = show.Slug;
            series.Status = MapSeriesStatus(show.Status);
            series.Ratings = MapRatings(show.Rating);
            series.Genres = show.Genres;

            if (show.ContentRating.IsNotNullOrWhiteSpace())
            {
                series.Certification = show.ContentRating.ToUpper();
            }
            
            series.Actors = show.Actors.Select(MapActors).ToList();
            series.Seasons = show.Seasons.Select(MapSeason).ToList();
            series.Images = show.Images.Select(MapImage).ToList();
            series.Monitored = true;

            return series;
        }

        private static Actor MapActors(ActorResource arg)
        {
            var newActor = new Actor
            {
                Name = arg.Name,
                Character = arg.Character
            };

            if (arg.Image != null)
            {
                newActor.Images = new List<MediaCover.MediaCover>
                {
                    new MediaCover.MediaCover(MediaCoverTypes.Headshot, arg.Image)
                };
            }

            return newActor;
        }

        private static Episode MapEpisode(EpisodeResource oracleEpisode)
        {
            var episode = new Episode();
            episode.Overview = oracleEpisode.Overview;
            episode.SeasonNumber = oracleEpisode.SeasonNumber;
            episode.EpisodeNumber = oracleEpisode.EpisodeNumber;
            episode.AbsoluteEpisodeNumber = oracleEpisode.AbsoluteEpisodeNumber;
            episode.Title = oracleEpisode.Title;

            episode.AirDate = oracleEpisode.AirDate;
            episode.AirDateUtc = oracleEpisode.AirDateUtc;

            episode.Ratings = MapRatings(oracleEpisode.Rating);

            //Don't include series fanart images as episode screenshot
            if (oracleEpisode.Image != null)
            {
                episode.Images.Add(new MediaCover.MediaCover(MediaCoverTypes.Screenshot, oracleEpisode.Image));
            }

            return episode;
        }

        private static Season MapSeason(SeasonResource seasonResource)
        {
            return new Season
            {
                SeasonNumber = seasonResource.SeasonNumber,
                Images = seasonResource.Images.Select(MapImage).ToList(),
                Monitored = seasonResource.SeasonNumber > 0
            };
        }

        private static SeriesStatusType MapSeriesStatus(string status)
        {
            if (status.Equals("ended", StringComparison.InvariantCultureIgnoreCase))
            {
                return SeriesStatusType.Ended;
            }

            return SeriesStatusType.Continuing;
        }

        private static Ratings MapRatings(RatingResource rating)
        {
            if (rating == null)
            {
                return new Ratings();
            }

            return new Ratings
            {
                Votes = rating.Count,
                Value = rating.Value
            };
        }

        private static MediaCover.MediaCover MapImage(ImageResource arg)
        {
            return new MediaCover.MediaCover
            {
                Url = arg.Url,
                CoverType = MapCoverType(arg.CoverType)
            };
        }

        private static MediaCoverTypes MapCoverType(string coverType)
        {
            switch (coverType.ToLower())
            {
                case "poster":
                    return MediaCoverTypes.Poster;
                case "banner":
                    return MediaCoverTypes.Banner;
                case "fanart":
                    return MediaCoverTypes.Fanart;
                default:
                    return MediaCoverTypes.Unknown;
            }
        }
    }
}
