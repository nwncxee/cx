//::///////////////////////////////////////////////
//:: nuicx_nui -- logs every NUI event the nuicx window sends, so the run can see the "cx" element's "refused" events and their payload
//:://////////////////////////////////////////////
#include "nw_inc_nui"

void main()
{
    string sType = NuiGetEventType();
    if (sType == "mouseup" || sType == "mousedown") return;
    WriteTimestampedLogEntry("CXSHOW: nui-event " + sType + " element=" + NuiGetEventElement() + " payload=" + JsonDump(NuiGetEventPayload()));
}
