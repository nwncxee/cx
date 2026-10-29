using System;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The instance took VK_EXT_debug_utils: submits and passes are labelled and objects named for capture
    /// tools. Off, labels are read past and nothing is named.</summary>
    private readonly bool _labels;
    /// <summary>Labels the current recording opened and has not closed, its own list's name the first; the submit
    /// closes them all before it ends.</summary>
    private int _labelDepth;
    /// <summary>A list that left a label open has been logged; once is enough to find it.</summary>
    private bool _labelLeakLogged;

    /// <summary>Name <paramref name="handle"/> for a capture tool, when the instance can.</summary>
    private void Name(VkObjectType type, ulong handle, string label)
    {
        if (_labels && label.Length > 0) _vki.vkSetDebugUtilsObjectNameEXT(_device, type, handle, label);
    }

    /// <summary><see cref="Op.BeginLabel"/>: the name is NUL-terminated in the arena.</summary>
    private bool Label(LabelCmd h, ref CommandReader r)
    {
        ReadOnlySpan<byte> name = r.Span<byte>(h.Bytes);
        if (_labels) OpenLabel(name);
        return true;
    }

    /// <summary><see cref="Op.EndLabel"/>: an end with nothing of the list's open in this command buffer is the tail
    /// of a region an earlier submit opened and closed, and is dropped.</summary>
    private bool Unlabel()
    {
        if (_labelDepth > 1) CloseLabel();
        return true;
    }

    private void OpenLabel(ReadOnlySpan<byte> nameZ)
    {
        fixed (byte* p = nameZ)
        {
            var l = new VkDebugUtilsLabelEXT { pLabelName = p };
            _vki.vkCmdBeginDebugUtilsLabelEXT(_cmd, &l);
        }
        _labelDepth++;
    }

    private void CloseLabel()
    {
        _vki.vkCmdEndDebugUtilsLabelEXT(_cmd);
        _labelDepth--;
    }
}
