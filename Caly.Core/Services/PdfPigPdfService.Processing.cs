﻿// Copyright (c) 2025 BobLd
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using Caly.Core.ViewModels;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Caly.Core.Services
{
    internal sealed partial class PdfPigPdfService
    {
        private enum RenderRequestTypes : byte
        {
            PageSize = 0,
            Picture = 1,
            Thumbnail = 2,
            TextLayer = 3
        }

        private sealed class RenderRequestComparer : IComparer<RenderRequest>
        {
            public static readonly RenderRequestComparer Instance = new RenderRequestComparer();

            public int Compare(RenderRequest? x, RenderRequest? y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (y is null) return 1;
                if (x is null) return -1;

                if (x.Page.PageNumber.Equals(y.Page.PageNumber))
                {
                    return x.Type.CompareTo(y.Type);
                }

                return x.Page.PageNumber.CompareTo(y.Page.PageNumber);
            }
        }

        private sealed class RenderRequest
        {
            public PdfPageViewModel Page { get; }

            public RenderRequestTypes Type { get; }

            public CancellationToken Token { get; }

            public RenderRequest(PdfPageViewModel page, RenderRequestTypes type, CancellationToken token)
            {
                Page = page;
                Type = type;
                Token = token;
            }
        }

        private readonly ChannelWriter<RenderRequest> _requestsWriter;
        private readonly ChannelReader<RenderRequest> _requestsReader;

        private readonly CancellationTokenSource _mainCts = new();

        private readonly Task _processingLoopTask;

        #region Loops
        private async Task ProcessingLoop()
        {
            Debug.ThrowOnUiThread();

            try
            {
                while (await _requestsReader.WaitToReadAsync(_mainCts.Token))
                {
                    var r = await _requestsReader.ReadAsync(_mainCts.Token);
                    try
                    {
                        switch (r.Type)
                        {
                            case RenderRequestTypes.PageSize:
                                await ProcessPageSizeRequest(r);
                                break;

                            case RenderRequestTypes.Picture:
                                await ProcessPictureRequest(r);
                                break;

                            case RenderRequestTypes.Thumbnail:
                                await ProcessThumbnailRequest(r);
                                break;

                            case RenderRequestTypes.TextLayer:
                                await ProcessTextLayerRequest(r);
                                break;
   
                            default:
                                throw new NotImplementedException(r.Type.ToString());
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        System.Diagnostics.Debug.WriteLine($"CANCELED: Page {r.Page.PageNumber}, type {r.Type}.");
                    }
                    catch (Exception e)
                    {
                        // We just ignore for the moment
                        Debug.WriteExceptionToFile(e);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
        #endregion

        #region Picture
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _pictureTokens = new();

        private async Task ProcessPictureRequest(RenderRequest renderRequest)
        {
            System.Diagnostics.Debug.WriteLine($"[RENDER] [PICTURE] Start process {renderRequest.Page.PageNumber}");

            try
            {
                renderRequest.Token.ThrowIfCancellationRequested();

                if (IsDisposed())
                {
                    return;
                }

                if (renderRequest.Page.PdfPicture is not null)
                {
                    System.Diagnostics.Debug.WriteLine($"[RENDER] [PICTURE] No need process {renderRequest.Page.PageNumber}");
                    return;
                }

                var picture = await GetRenderPageAsync(renderRequest.Page.PageNumber, renderRequest.Token);

                renderRequest.Page.PdfPicture = picture;

                if (renderRequest.Page.PdfPicture?.Item is not null)
                {
                    renderRequest.Page.Width = renderRequest.Page.PdfPicture.Item.CullRect.Width;
                    renderRequest.Page.Height = renderRequest.Page.PdfPicture.Item.CullRect.Height;
                }
            }
            finally
            {
                if (_pictureTokens.TryRemove(renderRequest.Page.PageNumber, out var cts))
                {
                    cts.Dispose();
                }
            }

            System.Diagnostics.Debug.WriteLine($"[RENDER] [PICTURE] End process {renderRequest.Page.PageNumber}");
        }

        public void AskPageSize(PdfPageViewModel page, CancellationToken token)
        {
            System.Diagnostics.Debug.WriteLine($"[RENDER] AskPageSize {page.PageNumber}");

            if (IsDisposed())
            {
                return;
            }
            
            // No cancel possible
            if (!_requestsWriter.TryWrite(new RenderRequest(page, RenderRequestTypes.PageSize, CancellationToken.None)))
            {
                throw new Exception("Could not write request to channel."); // Should never happen as unbounded channel
            }
        }

        public void AskPagePicture(PdfPageViewModel page, CancellationToken token)
        {
            System.Diagnostics.Debug.WriteLine($"[RENDER] AskPagePicture {page.PageNumber}");

            if (IsDisposed())
            {
                return;
            }

            var pageCts = CancellationTokenSource.CreateLinkedTokenSource(token, _mainCts.Token);

            if (_pictureTokens.TryAdd(page.PageNumber, pageCts))
            {
                if (!_requestsWriter.TryWrite(new RenderRequest(page, RenderRequestTypes.Picture, pageCts.Token)))
                {
                    throw new Exception("Could not write request to channel."); // Should never happen as unbounded channel
                }
            }
        }

        public void AskRemovePagePicture(PdfPageViewModel page)
        {
            System.Diagnostics.Debug.WriteLine($"[RENDER] AskRemovePagePicture {page.PageNumber}");

            var picture = page.PdfPicture;

            page.PdfPicture = null;
            if (_pictureTokens.TryRemove(page.PageNumber, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            picture?.Dispose();

            //System.Diagnostics.Debug.Assert((picture?.RefCount ?? 0) == 0);
        }
        #endregion

        #region Text layer
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _textLayerTokens = new();

        private async Task ProcessPageSizeRequest(RenderRequest renderRequest)
        {
            System.Diagnostics.Debug.WriteLine($"[RENDER] [SIZE] Start process {renderRequest.Page.PageNumber}");

            // No cancel possible
            
            try
            {
                renderRequest.Token.ThrowIfCancellationRequested();

                if (IsDisposed())
                {
                    return;
                }

                if (renderRequest.Page is { Height: > 0, Width: > 0 })
                {
                    System.Diagnostics.Debug.WriteLine($"[RENDER] [SIZE] No need process {renderRequest.Page.PageNumber}");
                    return;
                }

                await SetPageSizeAsync(renderRequest.Page, renderRequest.Token);
            }
            finally
            {
                if (_textLayerTokens.TryRemove(renderRequest.Page.PageNumber, out var cts))
                {
                    cts.Dispose();
                }
            }
            System.Diagnostics.Debug.WriteLine($"[RENDER] [SIZE] End process {renderRequest.Page.PageNumber}");
        }

        private async Task ProcessTextLayerRequest(RenderRequest renderRequest)
        {
            System.Diagnostics.Debug.WriteLine($"[RENDER] [TEXT] Start process {renderRequest.Page.PageNumber}");

            try
            {
                renderRequest.Token.ThrowIfCancellationRequested();

                if (IsDisposed())
                {
                    return;
                }

                if (renderRequest.Page.PdfTextLayer is not null)
                {
                    System.Diagnostics.Debug.WriteLine($"[RENDER] [TEXT] No need process {renderRequest.Page.PageNumber}");
                    return;
                }

                await SetPageTextLayer(renderRequest.Page, renderRequest.Token);
            }
            finally
            {
                if (_textLayerTokens.TryRemove(renderRequest.Page.PageNumber, out var cts))
                {
                    cts.Dispose();
                }
            }
            System.Diagnostics.Debug.WriteLine($"[RENDER] [TEXT] End process {renderRequest.Page.PageNumber}");
        }
        
        public void AskPageTextLayer(PdfPageViewModel page, CancellationToken token)
        {
            System.Diagnostics.Debug.WriteLine($"[RENDER] AskPageTextLayer {page.PageNumber}");

            if (IsDisposed())
            {
                return;
            }

            var pageCts = CancellationTokenSource.CreateLinkedTokenSource(token, _mainCts.Token);

            if (_textLayerTokens.TryAdd(page.PageNumber, pageCts))
            {
                if (!_requestsWriter.TryWrite(new RenderRequest(page, RenderRequestTypes.TextLayer, pageCts.Token)))
                {
                    throw new Exception("Could not write request to channel."); // Should never happen as unbounded channel
                }
            }
        }

        public void AskRemovePageTextLayer(PdfPageViewModel page)
        {
            System.Diagnostics.Debug.WriteLine($"[RENDER] AskRemovePageTextLayer {page.PageNumber}");

            if (_textLayerTokens.TryRemove(page.PageNumber, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
        #endregion

        #region Thumbnail
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _thumbnailTokens = new();

        private async Task ProcessThumbnailRequest(RenderRequest renderRequest)
        {
            System.Diagnostics.Debug.WriteLine($"[RENDER] [THUMBNAIL] Start process {renderRequest.Page.PageNumber}");

            try
            {
                renderRequest.Token.ThrowIfCancellationRequested();

                if (IsDisposed())
                {
                    return;
                }

                if (renderRequest.Page.Thumbnail is not null)
                {
                    System.Diagnostics.Debug.WriteLine($"[RENDER] [THUMBNAIL] No need process {renderRequest.Page.PageNumber}");
                    return;
                }

                var picture = renderRequest.Page.PdfPicture?.Clone();
                if (picture is not null)
                {
                    await SetThumbnail(renderRequest.Page, picture.Item, renderRequest.Token);
                    picture.Dispose();
                    return;
                }

                // Need to get picture first
                using (picture = await GetRenderPageAsync(renderRequest.Page.PageNumber, renderRequest.Token))
                {
                    if (picture is not null)
                    {
                        // This is the first we load the page, width and height are not set yet
                        renderRequest.Page.Width = picture.Item.CullRect.Width;
                        renderRequest.Page.Height = picture.Item.CullRect.Height;

                        await SetThumbnail(renderRequest.Page, picture.Item, renderRequest.Token);
                    }
                }
            }
            finally
            {
                if (_thumbnailTokens.TryRemove(renderRequest.Page.PageNumber, out var cts))
                {
                    cts.Dispose();
                }
            }

            System.Diagnostics.Debug.WriteLine($"[RENDER] [THUMBNAIL] End process {renderRequest.Page.PageNumber}");
        }

        public void AskPageThumbnail(PdfPageViewModel page, CancellationToken token)
        {
            System.Diagnostics.Debug.WriteLine($"[RENDER] AskPageThumbnail {page.PageNumber}");

            if (IsDisposed())
            {
                return;
            }

            var pageCts = CancellationTokenSource.CreateLinkedTokenSource(token, _mainCts.Token);
            
            //pageCts.Cancel();

            if (_thumbnailTokens.TryAdd(page.PageNumber, pageCts))
            {
                if (!_requestsWriter.TryWrite(new RenderRequest(page, RenderRequestTypes.Thumbnail, pageCts.Token)))
                {
                    throw new Exception("Could not write request to channel."); // Should never happen as unbounded channel
                }
            }

            System.Diagnostics.Debug.WriteLine($"[RENDER] Thumbnail Count {_bitmaps.Count}");
        }

        public void AskRemoveThumbnail(PdfPageViewModel page)
        {
            System.Diagnostics.Debug.WriteLine($"[RENDER] AskRemoveThumbnail {page.PageNumber}");

            var thumbnail = page.Thumbnail;
            page.Thumbnail = null;

            if (_thumbnailTokens.TryRemove(page.PageNumber, out var cts))
            {
                System.Diagnostics.Debug.WriteLine($"[RENDER] REMOVED {page.PageNumber}");
                cts.Cancel();
                cts.Dispose();
            }

            if (_bitmaps.TryRemove(page.PageNumber, out var vm))
            {
                // Should always be null
                //System.Diagnostics.Debug.Assert(vm.Thumbnail is null);
            }

            thumbnail?.Dispose();

            System.Diagnostics.Debug.WriteLine($"[RENDER] Thumbnail Count {_bitmaps.Count}");
        }
        #endregion
    }
}
