using System;
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

    private int _width;
    private int _height;

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    private struct Constants
    {
        public uint Width;
        public uint Height;
        public uint Pad1;
        public uint Pad2;
    }

    private const string ShaderSource = @"
Texture2D<float4> InputTexture : register(t0);
RWTexture2D<uint> OutputY : register(u0);
RWTexture2D<uint2> OutputUV : register(u1);

cbuffer Constants : register(b0)
{
    uint Width;
    uint Height;
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

    // Input is B8G8R8A8_UNorm
    float4 p00 = InputTexture[uint2(x, y)];
    float4 p10 = InputTexture[uint2(x + 1, y)];
    float4 p01 = InputTexture[uint2(x, y + 1)];
    float4 p11 = InputTexture[uint2(x + 1, y + 1)];

    // BT.601 limited range YUV conversion
    float y00 = 0.257 * p00.r + 0.504 * p00.g + 0.098 * p00.b + 0.0625;
    float y10 = 0.257 * p10.r + 0.504 * p10.g + 0.098 * p10.b + 0.0625;
    float y01 = 0.257 * p01.r + 0.504 * p01.g + 0.098 * p01.b + 0.0625;
    float y11 = 0.257 * p11.r + 0.504 * p11.g + 0.098 * p11.b + 0.0625;

    OutputY[uint2(x, y)] = (uint)clamp(y00 * 255.0, 0.0, 255.0);
    OutputY[uint2(x + 1, y)] = (uint)clamp(y10 * 255.0, 0.0, 255.0);
    OutputY[uint2(x, y + 1)] = (uint)clamp(y01 * 255.0, 0.0, 255.0);
    OutputY[uint2(x + 1, y + 1)] = (uint)clamp(y11 * 255.0, 0.0, 255.0);

    float4 avg = (p00 + p10 + p01 + p11) * 0.25;
    float u = -0.148 * avg.r - 0.291 * avg.g + 0.439 * avg.b + 0.5;
    float v =  0.439 * avg.r - 0.368 * avg.g - 0.071 * avg.b + 0.5;

    uint uU = (uint)clamp(u * 255.0, 0.0, 255.0);
    uint uV = (uint)clamp(v * 255.0, 0.0, 255.0);

    OutputUV[DTid.xy] = uint2(uU, uV);
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
        var bytecode = Compiler.Compile(ShaderSource, "CSMain", "ColorConverter", "cs_5_0");
        if (bytecode.IsEmpty)
            throw new Exception("Failed to compile ColorConverter compute shader");

        _computeShader = _device.CreateComputeShader(bytecode.Span);

        var cbDesc = new BufferDescription
        {
            ByteWidth = (uint)Marshal.SizeOf<Constants>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None
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

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };
        _nv12Texture = _device.CreateTexture2D(desc);

        var uavYDesc = new UnorderedAccessViewDescription
        {
            Format = Format.R8_UInt,
            ViewDimension = UnorderedAccessViewDimension.Texture2D,
            Texture2D = new Texture2DUnorderedAccessView { MipSlice = 0 }
        };
        _nv12UavY = _device.CreateUnorderedAccessView(_nv12Texture, uavYDesc);

        var uavUVDesc = new UnorderedAccessViewDescription
        {
            Format = Format.R8G8_UInt,
            ViewDimension = UnorderedAccessViewDimension.Texture2D,
            Texture2D = new Texture2DUnorderedAccessView { MipSlice = 0 }
        };
        _nv12UavUV = _device.CreateUnorderedAccessView(_nv12Texture, uavUVDesc);

        return _nv12Texture;
    }

    public void Convert(ID3D11ShaderResourceView inputSrv)
    {
        if (_computeShader == null || _nv12UavY == null || _nv12UavUV == null || _constantBuffer == null) return;

        var constants = new Constants
        {
            Width = (uint)_width,
            Height = (uint)_height
        };

        var mapped = _context.Map(_constantBuffer, 0, MapMode.WriteDiscard);
        Marshal.StructureToPtr(constants, mapped.DataPointer, false);
        _context.Unmap(_constantBuffer, 0);

        _context.CSSetShader(_computeShader);
        _context.CSSetConstantBuffer(0, _constantBuffer);
        _context.CSSetShaderResource(0, inputSrv);
        _context.CSSetUnorderedAccessView(0, _nv12UavY);
        _context.CSSetUnorderedAccessView(1, _nv12UavUV);

        uint threadGroupsX = ((uint)_width + 15) / 16;
        uint threadGroupsY = ((uint)_height + 15) / 16;
        _context.Dispatch(threadGroupsX, threadGroupsY, 1);

        _context.CSSetUnorderedAccessView(0, null);
        _context.CSSetUnorderedAccessView(1, null);
        _context.CSSetShaderResource(0, null);
    }

    public void Dispose()
    {
        _nv12UavY?.Dispose();
        _nv12UavUV?.Dispose();
        _nv12Texture?.Dispose();
        _constantBuffer?.Dispose();
        _computeShader?.Dispose();
    }
}
