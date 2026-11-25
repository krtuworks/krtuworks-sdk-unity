#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Krtuworks.Unity
{
    /// <summary>A player's picture: a remote https image loaded into a texture with the engine's web request, once per address.</summary>
    internal static class KrtuworksAvatars
    {
        static readonly Dictionary<string, Texture2D?> Loaded = new Dictionary<string, Texture2D?>();

        /// <summary>The picture, or null when the address is not https, nothing came back, or the engine cannot read the format.</summary>
        internal static Task<Texture2D?> LoadAsync(string address)
        {
            var url = UiFormat.EngineImage(address);
            if (!url.StartsWith("https://", StringComparison.Ordinal)) return Task.FromResult<Texture2D?>(null);
            if (Loaded.TryGetValue(url, out var known)) return Task.FromResult(known);
            var done = new TaskCompletionSource<Texture2D?>();
            var request = UnityWebRequestTexture.GetTexture(url);
            request.SendWebRequest().completed += _ =>
            {
                Texture2D? picture = null;
                try
                {
                    if (request.result == UnityWebRequest.Result.Success) picture = DownloadHandlerTexture.GetContent(request);
                }
                catch (Exception)
                {
                    picture = null;
                }
                finally
                {
                    request.Dispose();
                }
                Loaded[url] = picture;
                done.SetResult(picture);
            };
            return done.Task;
        }
    }
}
