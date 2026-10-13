// Substring variant of ~/ghidra_scripts/TracyFn.java: find defined strings CONTAINING each arg and walk
//   string <- tracy srcloc struct (data ref) <- code ref, printing the functions. This is how #446
//   anchored CNWCArea/CNWCAnimBase on Windows: nwmain.exe keeps its __FUNCSIG__ and ExoLog strings
//   even for classes it does not export, so a class the sweep dumps empty is still anchorable.
//   Run: tools/ghidra/headless.sh nwmain.exe TracySub.java out.txt "CNWCArea::LoadBorder"
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

/** args: outPath substr [substr...] -- like TracyFn but substring match. */
public class TracySub extends GhidraScript {
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        List<String> want = Arrays.asList(a).subList(1, a.length);
        DataIterator di = currentProgram.getListing().getDefinedData(true);
        while (di.hasNext()) {
            Data d = di.next();
            Object v = d.getValue();
            if (!(v instanceof String)) continue;
            String sv = (String) v;
            boolean hit = false;
            for (String wsub : want) if (sv.contains(wsub)) { hit = true; break; }
            if (!hit) continue;
            out.println("STRING \"" + sv + "\" @ " + d.getAddress());
            for (Reference r : getReferencesTo(d.getAddress())) {
                Address from = r.getFromAddress();
                Function f = getFunctionContaining(from);
                if (f != null) { out.println("   code <- " + from + " " + f.getName(true) + " @ " + f.getEntryPoint()); continue; }
                Data s = currentProgram.getListing().getDataContaining(from);
                Address st = s != null ? s.getAddress() : from;
                out.println("   data <- " + from + " (struct @ " + st + ")");
                for (Address cand : new Address[]{st, from, from.subtract(8), from.subtract(16), from.subtract(24)}) {
                    for (Reference r2 : getReferencesTo(cand)) {
                        Function f2 = getFunctionContaining(r2.getFromAddress());
                        out.println("      <- " + r2.getFromAddress() + " " + (f2 == null ? "(none)" : f2.getName(true) + " @ " + f2.getEntryPoint()));
                    }
                }
            }
        }
        out.close(); println("done");
    }
}
