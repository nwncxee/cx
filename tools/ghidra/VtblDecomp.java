// Decompile one slot of each vftable address (a std::function impl's _Do_call is slot 2), listing the first six slot targets.
//   VtblDecomp.java out.c <slot> 140e3b140 140e3b1b0
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import java.io.*;

public class VtblDecomp extends GhidraScript {
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter w = new PrintWriter(new BufferedWriter(new FileWriter(a[0])));
        int slot = Integer.parseInt(a[1]);
        DecompInterface ifc = new DecompInterface();
        ifc.setOptions(new DecompileOptions());
        ifc.setSimplificationStyle("decompile");
        ifc.openProgram(currentProgram);
        for (int i = 2; i < a.length; i++) {
            Address v = toAddr(a[i]);
            w.print("// ===== vftable " + v + " slots:");
            for (int k = 0; k < 6; k++) w.print(" " + toAddr(currentProgram.getMemory().getLong(v.add(8L * k))));
            w.println();
            Address t = toAddr(currentProgram.getMemory().getLong(v.add(8L * slot)));
            Function f = getFunctionAt(t);
            if (f == null) { w.println("// NO FUNCTION AT " + t); continue; }
            DecompileResults r = ifc.decompileFunction(f, 120, monitor);
            w.println(r.decompileCompleted() ? r.getDecompiledFunction().getC() : "// FAILED: " + r.getErrorMessage());
        }
        w.close();
    }
}
