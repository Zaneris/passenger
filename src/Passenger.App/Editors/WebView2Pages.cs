using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Avalonia.Platform;

namespace Passenger.App.Editors;

/// <summary>A WebView2's requests of <see cref="ServedPage.Host"/>, answered from the page it is shown
/// (<see cref="Serve"/>): WebView2 reads no folder for a page given as text and takes one of 2 MB at most, so the page and
/// its folder are served instead. The control's own WebResourceRequested has no way to answer, so the web view's COM
/// interfaces are called by their vtable slots, as WebView2.h numbers them. Every call is on the window's thread; a
/// response's bytes are read on any.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComClass]
internal sealed unsafe partial class WebView2Pages : IWebResourceRequestedHandler, IDisposable
{
    /// <summary>ICoreWebView2's add_WebResourceRequested, remove_WebResourceRequested, AddWebResourceRequestedFilter and
    /// RemoveWebResourceRequestedFilter; ICoreWebView2_2's get_Environment.</summary>
    private const int Listen = 55;
    private const int Unlisten = 56;
    private const int AddFilter = 57;
    private const int RemoveFilter = 58;
    private const int GetEnvironment = 67;

    /// <summary>ICoreWebView2Environment's CreateWebResourceResponse.</summary>
    private const int CreateResponse = 4;

    /// <summary>ICoreWebView2WebResourceRequestedEventArgs' get_Request and put_Response, ICoreWebView2WebResourceRequest's
    /// get_Uri.</summary>
    private const int GetRequest = 3;
    private const int PutResponse = 5;
    private const int GetUri = 3;

    /// <summary>COREWEBVIEW2_WEB_RESOURCE_CONTEXT_ALL.</summary>
    private const int EveryResource = 0;

    private const string Filter = $"https://{ServedPage.Host}/*";

    /// <summary>ICoreWebView2_2, whose environment makes the responses.</summary>
    private static readonly Guid Second = new("9E8F0CF8-E670-4B5E-B2BC-73E061E3184C");

    /// <summary>The ICoreWebView2 and its ICoreWebView2Environment, a reference of this one's own to each.</summary>
    private readonly nint web;
    private readonly nint environment;

    private long token;
    private bool listening;
    private bool gone;
    private ServedPage? page;

    private WebView2Pages(nint web, nint environment)
    {
        this.web = web;
        this.environment = environment;
    }

    /// <summary>The requests of the web view behind <paramref name="handle"/>, answered from the page given to
    /// <see cref="Serve"/>; null where the web view refuses, which is logged.</summary>
    public static WebView2Pages? Over(IWindowsWebView2PlatformHandle handle)
    {
        nint web = handle.CoreWebView2;
        if (web == 0)
        {
            return null;
        }
        nint environment = 0;
        int done = Marshal.QueryInterface(web, in Second, out nint second);
        if (done >= 0)
        {
            done = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(second, GetEnvironment))(second, &environment);
            Marshal.Release(second);
        }
        if (done < 0 || environment == 0)
        {
            Marshal.Release(web);
            IdeErrors.Write($"The HTML preview could not reach WebView2's environment (0x{done:X8}): its page is given as text");
            return null;
        }
        WebView2Pages pages = new(web, environment);
        done = pages.Hear();
        if (done < 0)
        {
            pages.Dispose();
            IdeErrors.Write($"WebView2 refused the HTML preview its requests (0x{done:X8}): its page is given as text");
            return null;
        }
        return pages;
    }

    /// <summary>Answers the host's requests from <paramref name="next"/> from now on.</summary>
    public void Serve(ServedPage next) => page = next;

    public int Invoke(nint sender, nint args)
    {
        if (gone)
        {
            return 0;
        }
        nint request = 0;
        char* address = null;
        nint content = 0;
        nint response = 0;
        try
        {
            int done = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(args, GetRequest))(args, &request);
            if (done >= 0)
            {
                done = ((delegate* unmanaged[Stdcall]<nint, char**, int>)Slot(request, GetUri))(request, &address);
            }
            if (done < 0)
            {
                return done;
            }
            PageAnswer answer = Uri.TryCreate(new string(address), UriKind.Absolute, out Uri? asked) && page is { } shown ? shown.Answer(asked) : PageAnswer.Missing;
            if (Opened(ref answer) is { } bytes)
            {
                content = (nint)ComInterfaceMarshaller<IStream>.ConvertToUnmanaged(new ReadStream(bytes));
            }
            fixed (char* reason = answer.Reason)
            fixed (char* headers = answer.Headers)
            {
                done = ((delegate* unmanaged[Stdcall]<nint, nint, int, char*, char*, nint*, int>)Slot(environment, CreateResponse))(
                    environment, content, answer.Status, reason, headers, &response);
            }
            return done < 0 ? done : ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(args, PutResponse))(args, response);
        }
        catch (Exception error)
        {
            // WebView2 calls in from native code, which no exception may unwind into: the request goes unanswered.
            IdeErrors.Write($"The HTML preview could not answer a request of its page: {error}");
            return error.HResult;
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)address);
            Release(content);
            Release(response);
            Release(request);
        }
    }

    public void Dispose()
    {
        if (gone)
        {
            return;
        }
        gone = true;
        page = null;
        if (listening)
        {
            fixed (char* filter = Filter)
            {
                ((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Slot(web, RemoveFilter))(web, filter, EveryResource);
            }
            ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(web, Unlisten))(web, token);
        }
        Release(environment);
        Release(web);
    }

    /// <summary>This one made the web view's handler of requests on the host; an HRESULT, negative where it refused.</summary>
    private int Hear()
    {
        nint handler = (nint)ComInterfaceMarshaller<IWebResourceRequestedHandler>.ConvertToUnmanaged(this);
        long registered;
        int done = ((delegate* unmanaged[Stdcall]<nint, nint, long*, int>)Slot(web, Listen))(web, handler, &registered);
        ComInterfaceMarshaller<IWebResourceRequestedHandler>.Free((void*)handler);
        if (done < 0)
        {
            return done;
        }
        token = registered;
        listening = true;
        fixed (char* filter = Filter)
        {
            return ((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Slot(web, AddFilter))(web, filter, EveryResource);
        }
    }

    /// <summary>The answer's bytes; a file gone or locked since it was found answers that nothing is there.</summary>
    private static Stream? Opened(ref PageAnswer answer)
    {
        try
        {
            return answer.Open();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            answer = PageAnswer.Missing;
            return null;
        }
    }

    private static void* Slot(nint self, int index) => (*(void***)self)[index];

    private static void Release(nint reference)
    {
        if (reference != 0)
        {
            Marshal.Release(reference);
        }
    }
}

/// <summary>ICoreWebView2WebResourceRequestedEventHandler, its sender and arguments the web view's own pointers.</summary>
[GeneratedComInterface]
[Guid("AB00B74C-15F1-4646-80E8-E76341D25D71")]
internal partial interface IWebResourceRequestedHandler
{
    [PreserveSig]
    int Invoke(nint sender, nint args);
}

/// <summary>COM's ISequentialStream.</summary>
[GeneratedComInterface]
[Guid("0C733A30-2A1C-11CE-ADE5-00AA0044773D")]
internal unsafe partial interface ISequentialStream
{
    [PreserveSig]
    int Read(byte* buffer, uint count, uint* read);

    [PreserveSig]
    int Write(byte* buffer, uint count, uint* written);
}

/// <summary>COM's IStream.</summary>
[GeneratedComInterface]
[Guid("0000000C-0000-0000-C000-000000000046")]
internal unsafe partial interface IStream : ISequentialStream
{
    [PreserveSig]
    int Seek(long move, uint origin, ulong* position);

    [PreserveSig]
    int SetSize(ulong size);

    [PreserveSig]
    int CopyTo(nint target, ulong count, ulong* read, ulong* written);

    [PreserveSig]
    int Commit(uint flags);

    [PreserveSig]
    int Revert();

    [PreserveSig]
    int LockRegion(ulong offset, ulong count, uint kind);

    [PreserveSig]
    int UnlockRegion(ulong offset, ulong count, uint kind);

    [PreserveSig]
    int Stat(StreamStat* stat, uint flags);

    [PreserveSig]
    int Clone(nint* copy);
}

/// <summary>COM's STATSTG.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StreamStat
{
    public nint Name;
    public uint Type;
    public ulong Size;
    public long Modified;
    public long Created;
    public long Accessed;
    public uint Mode;
    public uint LocksSupported;
    public Guid Class;
    public uint StateBits;
    public uint Reserved;
}

/// <summary>A response's bytes as WebView2 reads them, from its own threads, to the end, where the file is let go.</summary>
[GeneratedComClass]
internal sealed unsafe partial class ReadStream(Stream bytes) : IStream
{
    private const int Done = 0;
    private const int Short = 1;
    private const int NotImplemented = unchecked((int)0x80004001);
    private const int Refused = unchecked((int)0x80030005);
    private const int NoSeek = unchecked((int)0x80030001);
    private const int Faulted = unchecked((int)0x8003001E);

    /// <summary>STGTY_STREAM.</summary>
    private const uint StreamType = 2;

    private readonly Lock gate = new();
    private readonly long length = bytes.Length;
    private bool ended;

    public int Read(byte* buffer, uint count, uint* read)
    {
        lock (gate)
        {
            int got = 0;
            try
            {
                if (!ended)
                {
                    got = bytes.ReadAtLeast(new Span<byte>(buffer, (int)Math.Min(count, int.MaxValue)), (int)Math.Min(count, int.MaxValue), throwOnEndOfStream: false);
                    if (got < count)
                    {
                        ended = true;
                        bytes.Dispose();
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Faulted;
            }
            if (read != null)
            {
                *read = (uint)got;
            }
            return got < count ? Short : Done;
        }
    }

    public int Write(byte* buffer, uint count, uint* written) => Refused;

    public int Seek(long move, uint origin, ulong* position)
    {
        lock (gate)
        {
            if (ended)
            {
                // Read to its end and let go: only where it stands can still be asked.
                if (position != null)
                {
                    *position = (ulong)length;
                }
                return move == 0 && origin != (uint)SeekOrigin.Begin ? Done : NoSeek;
            }
            try
            {
                long at = bytes.Seek(move, (SeekOrigin)origin);
                if (position != null)
                {
                    *position = (ulong)at;
                }
                return Done;
            }
            catch (Exception error) when (error is IOException or ArgumentException)
            {
                return NoSeek;
            }
        }
    }

    public int SetSize(ulong size) => Refused;

    public int CopyTo(nint target, ulong count, ulong* read, ulong* written) => NotImplemented;

    public int Commit(uint flags) => Done;

    public int Revert() => NotImplemented;

    public int LockRegion(ulong offset, ulong count, uint kind) => NotImplemented;

    public int UnlockRegion(ulong offset, ulong count, uint kind) => NotImplemented;

    public int Stat(StreamStat* stat, uint flags)
    {
        *stat = new StreamStat { Type = StreamType, Size = (ulong)length };
        return Done;
    }

    public int Clone(nint* copy) => NotImplemented;
}
