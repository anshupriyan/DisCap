using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.DXGI;

namespace Discap.Host.Capture;

public sealed class ColorConverter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    private ID3D11ComputeShader? _computeShader;
    private ID3D11Texture2D? _nv12Texture;
    private ID3D11UnorderedAccessView? _nv12UavY;
    private ID3D11UnorderedAccessView? _nv12UavUV;
    private ID3D11Buffer? _constantBuffer;
    
    private ID3D11Texture2D? _cursorTexture;
    private ID3D11ShaderResourceView? _cursorSrv;

    private int _width;
    private int _height;

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    private struct Constants
    {
        public uint Width;
        public uint Height;
        public int CursorX;
        public int CursorY;
        
        public uint CursorWidth;
        public uint CursorHeight;
        public uint Pad1;
        public uint Pad2;
    }

    private const string ShaderSource = @"
Texture2D<float4> InputTexture : register(t0);
Texture2D<float4> CursorTexture : register(t1);
RWTexture2D<uint> OutputY : register(u0);
RWTexture2D<uint2> OutputUV : register(u1);

cbuffer Constants : register(b0)
{
    uint Width;
    uint Height;
    int CursorX;
    int CursorY;
    uint CursorWidth;
    uint CursorHeight;
    uint Pad1;
    uint Pad2;
};

[numthreads(8, 8, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    uint x = DTid.x * 2;
    uint y = DTid.y * 2;

    if (x >= Width || y >= Height)
        return;

    // Input is B8G8R8A8_UNorm. 
    // In HLSL sampling for B8G8R8A8_UNorm: .r = Blue, .g = Green, .b = Red, .a = Alpha
    float4 p00 = InputTexture[uint2(x, y)];
    float4 p10 = InputTexture[uint2(x + 1, y)];
    float4 p01 = InputTexture[uint2(x, y + 1)];
    float4 p11 = InputTexture[uint2(x + 1, y + 1)];

    if (CursorWidth > 0)
    {
        int ix = (int)x;
        int iy = (int)y;
        if (ix >= CursorX && ix < CursorX + (int)CursorWidth && iy >= CursorY && iy < CursorY + (int)CursorHeight)
        {
            float4 c = CursorTexture[uint2(x - CursorX, y - CursorY)];
            if (c.w == 0.0 && c.z > 0.5) p00 = 1.0 - p00;
            else p00 = p00 * (1.0 - c.w) + c;
        }
        if (ix + 1 >= CursorX && ix + 1 < CursorX + (int)CursorWidth && iy >= CursorY && iy < CursorY + (int)CursorHeight)
        {
            float4 c = CursorTexture[uint2(x + 1 - CursorX, y - CursorY)];
            if (c.w == 0.0 && c.z > 0.5) p10 = 1.0 - p10;
            else p10 = p10 * (1.0 - c.w) + c;
        }
        if (ix >= CursorX && ix < CursorX + (int)CursorWidth && iy + 1 >= CursorY && iy + 1 < CursorY + (int)CursorHeight)
        {
            float4 c = CursorTexture[uint2(x - CursorX, y + 1 - CursorY)];
            if (c.w == 0.0 && c.z > 0.5) p01 = 1.0 - p01;
            else p01 = p01 * (1.0 - c.w) + c;
        }
        if (ix + 1 >= CursorX && ix + 1 < CursorX + (int)CursorWidth && iy + 1 >= CursorY && iy + 1 < CursorY + (int)CursorHeight)
        {
            float4 c = CursorTexture[uint2(x + 1 - CursorX, y + 1 - CursorY)];
            if (c.w == 0.0 && c.z > 0.5) p11 = 1.0 - p11;
            else p11 = p11 * (1.0 - c.w) + c;
        }
    }

    // Extract true R, G, B components from B8G8R8A8_UNorm (.r = Blue, .g = Green, .b = Red)
    float r00 = p00.b; float g00 = p00.g; float b00 = p00.r;
    float r10 = p10.b; float g10 = p10.g; float b10 = p10.r;
    float r01 = p01.b; float g01 = p01.g; float b01 = p01.r;
    float r11 = p11.b; float g11 = p11.g; float b11 = p11.r;

    // BT.709 Full Range (0-255 sRGB) YUV conversion
    float y00 = saturate(0.2126 * r00 + 0.7152 * g00 + 0.0722 * b00);
    float y10 = saturate(0.2126 * r10 + 0.7152 * g10 + 0.0722 * b10);
    float y01 = saturate(0.2126 * r01 + 0.7152 * g01 + 0.0722 * b01);
    float y11 = saturate(0.2126 * r11 + 0.7152 * g11 + 0.0722 * b11);

    OutputY[uint2(x, y)] = (uint)(y00 * 255.0 + 0.5);
    OutputY[uint2(x + 1, y)] = (uint)(y10 * 255.0 + 0.5);
    OutputY[uint2(x, y + 1)] = (uint)(y01 * 255.0 + 0.5);
    OutputY[uint2(x + 1, y + 1)] = (uint)(y11 * 255.0 + 0.5);

    float rAvg = (r00 + r10 + r01 + r11) * 0.25;
    float gAvg = (g00 + g10 + g01 + g11) * 0.25;
    float bAvg = (b00 + b10 + b01 + b11) * 0.25;

    float u = saturate(-0.1146 * rAvg - 0.3854 * gAvg + 0.5000 * bAvg + 0.5);
    float v = saturate( 0.5000 * rAvg - 0.4542 * gAvg - 0.0458 * bAvg + 0.5);

    OutputUV[DTid.xy] = uint2((uint)(v * 255.0 + 0.5), (uint)(u * 255.0 + 0.5));
}
";

    public ColorConverter(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        InitializeShader();
    }

    private void InitializeShader()
    {
        try
        {
            var bytecode = Compiler.Compile(ShaderSource, "CSMain", "ColorConverter", "cs_5_0");
            _computeShader = _device.CreateComputeShader(bytecode.ToArray());
        }
        catch (Exception ex)
        {
            throw new Exception($"Shader compilation failed: {ex.Message}");
        }

        var cbDesc = new BufferDescription
        {
            ByteWidth = (uint)Marshal.SizeOf<Constants>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
            StructureByteStride = 0
        };
        _constantBuffer = _device.CreateBuffer(cbDesc);
    }

    public ID3D11Texture2D EnsureOutputTexture(int width, int height)
    {
        if (_nv12Texture != null && _width == width && _height == height)
            return _nv12Texture;

        _nv12UavY?.Dispose();
        _nv12UavUV?.Dispose();
        _nv12Texture?.Dispose();

        _width = width;
        _height = height;

        var texDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.UnorderedAccess,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };

        _nv12Texture = _device.CreateTexture2D(texDesc);
        
        var yUavDesc = new UnorderedAccessViewDescription
        {
            Format = Format.R8_UInt,
            ViewDimension = UnorderedAccessViewDimension.Texture2D,
            Texture2D = new Texture2DUnorderedAccessView { MipSlice = 0 }
        };
        _nv12UavY = _device.CreateUnorderedAccessView(_nv12Texture, yUavDesc);

        var uvUavDesc = new UnorderedAccessViewDescription
        {
            Format = Format.R8G8_UInt,
            ViewDimension = UnorderedAccessViewDimension.Texture2D,
            Texture2D = new Texture2DUnorderedAccessView { MipSlice = 0 }
        };
        _nv12UavUV = _device.CreateUnorderedAccessView(_nv12Texture, uvUavDesc);

        return _nv12Texture;
    }

    public void UpdateCursor(int width, int height, ReadOnlySpan<byte> rgbaData)
    {
        _cursorSrv?.Dispose();
        _cursorTexture?.Dispose();
        _cursorSrv = null;
        _cursorTexture = null;

        if (width == 0 || height == 0 || rgbaData.IsEmpty) return;

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };

        unsafe
        {
            fixed (byte* ptr = rgbaData)
            {
                var data = new SubresourceData((nint)ptr, (uint)(width * 4), 0);
                _cursorTexture = _device.CreateTexture2D(desc, new[] { data });
            }
        }

        _cursorSrv = _device.CreateShaderResourceView(_cursorTexture);
    }

    public void Convert(ID3D11ShaderResourceView inputSrv, int cursorX, int cursorY, int cursorW, int cursorH)
    {
        if (_computeShader == null || _nv12UavY == null || _nv12UavUV == null || _constantBuffer == null) return;

        var constants = new Constants
        {
            Width = (uint)_width,
            Height = (uint)_height,
            CursorX = cursorX,
            CursorY = cursorY,
            CursorWidth = _cursorSrv != null ? (uint)cursorW : 0,
            CursorHeight = _cursorSrv != null ? (uint)cursorH : 0
        };

        var mapped = _context.Map(_constantBuffer, 0, MapMode.WriteDiscard);
        unsafe
        {
            System.Buffer.MemoryCopy(&constants, mapped.DataPointer.ToPointer(), sizeof(Constants), sizeof(Constants));
        }
        _context.Unmap(_constantBuffer, 0);

        _context.CSSetShader(_computeShader);
        _context.CSSetConstantBuffer(0, _constantBuffer);
        _context.CSSetShaderResource(0, inputSrv);
        
        if (_cursorSrv != null)
        {
            _context.CSSetShaderResource(1, _cursorSrv);
        }

        _context.CSSetUnorderedAccessView(0, _nv12UavY);
        _context.CSSetUnorderedAccessView(1, _nv12UavUV);

        uint groupX = (uint)(_width + 15) / 16;
        uint groupY = (uint)(_height + 15) / 16;
        _context.Dispatch(groupX, groupY, 1);

        // Cleanup state
        _context.CSSetShaderResource(0, null);
        _context.CSSetShaderResource(1, null);
        _context.CSSetUnorderedAccessView(0, null);
        _context.CSSetUnorderedAccessView(1, null);
    }

    public void Dispose()
    {
        _computeShader?.Dispose();
        _nv12Texture?.Dispose();
        _nv12UavY?.Dispose();
        _nv12UavUV?.Dispose();
        _constantBuffer?.Dispose();
        _cursorTexture?.Dispose();
        _cursorSrv?.Dispose();
    }
}
