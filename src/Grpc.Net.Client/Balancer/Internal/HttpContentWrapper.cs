#region Copyright notice and license

// Copyright 2019 The gRPC Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#endregion

using System.Net;

namespace Grpc.Net.Client.Balancer.Internal;

internal sealed class HttpContentWrapper : HttpContent
{
    private readonly HttpContent _inner;
    private readonly Action _disposeAction;
    private bool _disposed;

    public HttpContentWrapper(HttpContent inner, Action disposeAction)
    {
        _inner = inner;
        _disposeAction = disposeAction;

        foreach (var kvp in inner.Headers)
        {
            Headers.TryAddWithoutValidation(kvp.Key, kvp.Value.ToArray());
        }
    }

    ~HttpContentWrapper()
    {
        Dispose(false);
    }

#if NET5_0_OR_GREATER
    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using var content = _inner.ReadAsStream(cancellationToken);
        content.CopyTo(stream);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var content = await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (content.ConfigureAwait(false))
        {
            await content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }
#endif

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var content = await _inner.ReadAsStreamAsync().ConfigureAwait(false);
#if NET5_0_OR_GREATER
        await using (content.ConfigureAwait(false))
#else
        using (content)
#endif
        {
            await content.CopyToAsync(stream).ConfigureAwait(false);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!_disposed)
        {
            _disposeAction();
            _disposed = true;
        }

        if (disposing)
        {
            _inner.Dispose();
        }
    }
}
