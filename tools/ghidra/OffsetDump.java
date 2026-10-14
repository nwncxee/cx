// Dump, for every selected function, the constant displacements its body applies to each
// incoming parameter -- the decompiler's dataflow view, so a register-promoted `this` and an
// inlined accessor both still resolve to (parameter, displacement).
//
// Headless: tools/ghidra/dump-offsets.sh <prog> <out.tsv> [selector...]
//
// Selectors (any number; none = every named, non-thunk function):
//   class:CNWCCreature     every function in that namespace
//   addr:14080b240         one function by entry point (hex, image-based address)
//   all                    every named function in the program
//
// Output is TSV, one record per line:
//   P  program  imagebase  compiler
//   F  entry  namespace  name  nparams  callconv  status  thisreg  thisindex
//   T  entry  param  pointee-type-name                     -- what the parameter points at
//   A  entry  order  param  total  terminal  size          -- a constant displacement off a param
//   V  entry  param  slot                                  -- an indirect call through *(param+0)+slot
//   U  entry  order  reason                                -- a displacement we could NOT root
//   S  key  count                                          -- summary counters

import ghidra.app.decompiler.*;
import ghidra.app.decompiler.parallel.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.DataType;
import ghidra.program.model.data.Pointer;
import ghidra.program.model.listing.*;
import ghidra.program.model.pcode.*;
import ghidra.program.model.symbol.*;
import ghidra.util.task.TaskMonitor;

import java.io.PrintWriter;
import java.util.*;
import java.util.concurrent.ConcurrentHashMap;

public class OffsetDump extends GhidraScript {

    /// <summary>Root of a displacement chain: the varnode the chain bottoms out on, plus the sum.</summary>
    private static final class Root {
        final Varnode vn; final long disp;
        Root(Varnode vn, long disp) { this.vn = vn; this.disp = disp; }
    }

    private static final Map<String, Integer> COUNTERS = new ConcurrentHashMap<>();
    private static void bump(String k) { COUNTERS.merge(k, 1, Integer::sum); }

    /// <summary>Decompiler budget per function, in seconds. A handful of giant bodies blow past it.</summary>
    private static final int TIMEOUT_SECONDS = 60;

    @Override public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 1) { println("OffsetDump: need an output path"); return; }
        String out = args[0];

        Set<String> classes = new HashSet<>();
        Set<Long> addrs = new HashSet<>();
        boolean all = args.length < 2;
        for (int i = 1; i < args.length; i++) {
            String a = args[i];
            if (a.equals("all")) all = true;
            else if (a.startsWith("class:")) classes.add(a.substring(6));
            else if (a.startsWith("addr:")) addrs.add(Long.parseLong(a.substring(5), 16));
        }

        List<Function> picked = new ArrayList<>();
        FunctionIterator it = currentProgram.getFunctionManager().getFunctions(true);
        while (it.hasNext()) {
            Function f = it.next();
            if (f.isThunk() || f.isExternal()) continue;
            long ep = f.getEntryPoint().getOffset();
            boolean named = f.getSymbol().getSource() != SourceType.DEFAULT;
            Namespace ns = f.getParentNamespace();
            String cls = ns.isGlobal() ? "" : ns.getName(true);
            if (addrs.contains(ep) || (!classes.isEmpty() && classes.contains(cls)) || (all && named)) {
                picked.add(f);
            }
        }
        println("OffsetDump: " + picked.size() + " functions selected");

        DecompileOptions opts = new DecompileOptions();
        opts.grabFromProgram(currentProgram);
        DecompileConfigurer cfg = d -> {
            d.toggleCCode(false);
            d.toggleSyntaxTree(true);
            d.setSimplificationStyle("decompile");
            d.setOptions(opts);
        };
        DecompilerCallback<String> cb = new DecompilerCallback<>(currentProgram, cfg) {
            @Override public String process(DecompileResults r, TaskMonitor m) { return analyse(r); }
        };
        cb.setTimeout(TIMEOUT_SECONDS);

        long t0 = System.currentTimeMillis();
        try (PrintWriter w = new PrintWriter(out)) {
            w.println("P\t" + currentProgram.getName() + "\t"
                + Long.toHexString(currentProgram.getImageBase().getOffset()) + "\t"
                + currentProgram.getCompilerSpec().getCompilerSpecID());
            try {
                // Stream: 38k function records held in a List costs more than the 2G default heap.
                ParallelDecompiler.decompileFunctions(cb, currentProgram, picked.iterator(), s -> {
                    if (s != null && !s.isEmpty()) synchronized (w) { w.print(s); }
                }, monitor);
            } finally {
                cb.dispose();
            }
            long ms = System.currentTimeMillis() - t0;
            for (Map.Entry<String, Integer> e : new TreeMap<>(COUNTERS).entrySet())
                w.println("S\t" + e.getKey() + "\t" + e.getValue());
            w.println("S\tfunctions\t" + picked.size());
            w.println("S\tdecompile_ms\t" + ms);
            println("OffsetDump: " + picked.size() + " functions in " + ms + " ms -> " + out);
        }
    }

    private String analyse(DecompileResults r) {
        Function f = r.getFunction();
        Address ep = f.getEntryPoint();
        String hex = Long.toHexString(ep.getOffset());
        Namespace ns = f.getParentNamespace();
        String cls = ns.isGlobal() ? "-" : ns.getName(true);
        StringBuilder sb = new StringBuilder();

        HighFunction hf = r.getHighFunction();
        if (hf == null) {
            bump(r.isTimedOut() ? "decompile_timeout" : "decompile_failed");
            sb.append("F\t").append(hex).append('\t').append(cls).append('\t').append(f.getName())
              .append('\t').append(f.getParameterCount()).append('\t').append(f.getCallingConventionName())
              .append("\tnohigh\t-\t-1\n");
            return sb.toString();
        }

        LocalSymbolMap lsm = hf.getLocalSymbolMap();
        int np = lsm.getNumParams();
        Map<Varnode, Integer> paramOf = new HashMap<>();
        int thisIndex = -1;
        for (int i = 0; i < np; i++) {
            HighSymbol hs = lsm.getParamSymbol(i);
            if (hs != null && hs.isThisPointer() && thisIndex < 0) thisIndex = i;
            HighVariable hv = lsm.getParam(i);
            if (hv == null) { bump("param_no_highvar"); continue; }
            for (Varnode vn : hv.getInstances()) paramOf.put(vn, i);
        }
        // An anchored FUN_* has no applied prototype, so nothing is marked `this`. Slot 0 is the
        // fallback here; pair-offsets.py refines it from the T records and the access counts,
        // because a SysV body taking a by-value aggregate first carries `this` further along.
        if (thisIndex < 0 && np > 0) { thisIndex = 0; bump("thisindex_assumed"); }
        else if (thisIndex >= 0) bump("thisindex_marked");

        String thisreg = "-";
        Parameter p0 = f.getParameterCount() > thisIndex && thisIndex >= 0 ? f.getParameter(thisIndex) : null;
        if (p0 != null && p0.getRegister() != null) thisreg = p0.getRegister().getName();

        sb.append("F\t").append(hex).append('\t').append(cls).append('\t').append(f.getName())
          .append('\t').append(np).append('\t').append(f.getCallingConventionName())
          .append("\tok\t").append(thisreg).append('\t').append(thisIndex).append('\n');
        if (np == 0) bump("no_params_recovered");

        // A displacement belongs to the class the parameter POINTS AT, which is not always the
        // class that owns the function -- that is what lets a `Part*` argument carry Part's layout.
        for (int i = 0; i < f.getParameterCount() && i < np; i++) {
            Parameter pp = f.getParameter(i);
            if (pp == null) continue;
            DataType dt = pp.getDataType();
            if (!(dt instanceof Pointer)) continue;
            DataType pt = ((Pointer) dt).getDataType();
            if (pt == null) continue;
            String tn = pt.getName();
            if (tn == null || tn.isEmpty() || tn.startsWith("undefined") || tn.equals("void")) continue;
            sb.append("T\t").append(hex).append('\t').append(i).append('\t').append(tn).append('\n');
        }

        // Pass 1: every dereference. A field at +0 produces NO arithmetic op at all -- `mov [rdi],0`
        // is a bare STORE -- so the load/store address is the only place it can be seen.
        Set<Varnode> asAddress = new HashSet<>();
        List<PcodeOp> derefs = new ArrayList<>();
        Iterator<PcodeOpAST> ops = hf.getPcodeOps();
        List<PcodeOpAST> allOps = new ArrayList<>();
        while (ops.hasNext()) allOps.add(ops.next());
        for (PcodeOp op : allOps) {
            if (op.getOpcode() == PcodeOp.LOAD || op.getOpcode() == PcodeOp.STORE) {
                asAddress.add(op.getInput(1));
                derefs.add(op);
            }
        }

        int order = 0;
        for (PcodeOp op : derefs) {
            Root root = trace(op.getInput(1), 0);
            Integer pi = paramOf.get(root.vn);
            order++;
            int size = op.getOpcode() == PcodeOp.LOAD
                    ? (op.getOutput() != null ? op.getOutput().getSize() : 0)
                    : (op.getInput(2) != null ? op.getInput(2).getSize() : 0);
            emit(sb, hex, order, pi, root, size, true);
        }

        // Pass 2: pointers that are formed but not dereferenced here -- `lea` of a subobject, a
        // field address handed to a callee. Skipped when the only use was the load/store above.
        for (PcodeOp op : allOps) {
            int code = op.getOpcode();
            boolean disp = (code == PcodeOp.PTRSUB || code == PcodeOp.INT_ADD)
                    && op.getInput(1) != null && op.getInput(1).isConstant();
            boolean ptradd = code == PcodeOp.PTRADD && op.getInput(1) != null
                    && op.getInput(1).isConstant() && op.getInput(2) != null && op.getInput(2).isConstant();
            if (code == PcodeOp.CALLIND) {
                long[] vs = vtableSlot(op.getInput(0), paramOf);
                if (vs != null) { sb.append("V\t").append(hex).append('\t').append(vs[0])
                        .append('\t').append(vs[1]).append('\n'); bump("vslot"); }
                else bump("callind_unrooted");
                continue;
            }
            if (!disp && !ptradd) continue;
            if (op.getOutput() != null && asAddress.contains(op.getOutput())) continue;

            long terminal = disp ? signed(op.getInput(1))
                                 : signed(op.getInput(1)) * op.getInput(2).getOffset();
            Root root = trace(op.getInput(0), terminal);
            Integer pi = paramOf.get(root.vn);
            order++;
            emit(sb, hex, order, pi, root, 0, false);
        }
        return sb.toString();
    }

    private void emit(StringBuilder sb, String hex, int order, Integer pi, Root root, int size, boolean deref) {
        if (pi == null) {
            String why = reason(root.vn);
            sb.append("U\t").append(hex).append('\t').append(order).append('\t').append(why).append('\n');
            bump("unrooted_" + why);
            return;
        }
        if (root.disp < 0 || root.disp > 0x10000) { bump("disp_out_of_range"); return; }
        sb.append("A\t").append(hex).append('\t').append(order).append('\t').append(pi)
          .append('\t').append(root.disp).append('\t').append(root.disp)
          .append('\t').append(size).append('\n');
        bump(deref ? "acc_deref" : "acc_addr");
    }

    /// <summary>Sign-extend a constant varnode to a Java long using its own size.</summary>
    private static long signed(Varnode c) {
        long v = c.getOffset();
        int bits = c.getSize() * 8;
        if (bits >= 64) return v;
        long m = 1L << (bits - 1);
        return (v ^ m) - m;
    }

    /// <summary>Walk a pointer expression back to its root, summing constant displacements.</summary>
    private Root trace(Varnode v, long acc) {
        for (int guard = 0; guard < 64; guard++) {
            PcodeOp d = v.getDef();
            if (d == null) return new Root(v, acc);
            int c = d.getOpcode();
            if (c == PcodeOp.COPY || c == PcodeOp.CAST) { v = d.getInput(0); continue; }
            if ((c == PcodeOp.PTRSUB || c == PcodeOp.INT_ADD)
                    && d.getInput(1) != null && d.getInput(1).isConstant()) {
                acc += signed(d.getInput(1)); v = d.getInput(0); continue;
            }
            if (c == PcodeOp.INT_SUB && d.getInput(1) != null && d.getInput(1).isConstant()) {
                acc -= signed(d.getInput(1)); v = d.getInput(0); continue;
            }
            if (c == PcodeOp.PTRADD && d.getInput(1) != null && d.getInput(1).isConstant()
                    && d.getInput(2) != null && d.getInput(2).isConstant()) {
                acc += signed(d.getInput(1)) * d.getInput(2).getOffset(); v = d.getInput(0); continue;
            }
            return new Root(v, acc);
        }
        return new Root(v, acc);
    }

    /// <summary>An indirect call target of the form *( *(param+0) + slot ) -- a virtual dispatch.</summary>
    private long[] vtableSlot(Varnode target, Map<Varnode, Integer> paramOf) {
        Root t = trace(target, 0);
        PcodeOp d = t.vn.getDef();
        if (d == null || d.getOpcode() != PcodeOp.LOAD) return null;
        Root a = trace(d.getInput(1), 0);
        PcodeOp d2 = a.vn.getDef();
        if (d2 == null || d2.getOpcode() != PcodeOp.LOAD) return null;
        Root o = trace(d2.getInput(1), 0);
        if (o.disp != 0) return null;
        Integer pi = paramOf.get(o.vn);
        if (pi == null) return null;
        return new long[]{pi, a.disp};
    }

    /// <summary>Why a displacement could not be rooted on a parameter -- the residue taxonomy.</summary>
    private static String reason(Varnode root) {
        PcodeOp d = root.getDef();
        if (root.isConstant()) return "const";
        if (d == null) return root.isRegister() ? "reg" : (root.isAddress() ? "global" : "nodef");
        switch (d.getOpcode()) {
            case PcodeOp.LOAD: return "load";
            case PcodeOp.MULTIEQUAL: return "phi";
            case PcodeOp.INDIRECT: return "indirect";
            case PcodeOp.CALL: case PcodeOp.CALLIND: return "callresult";
            case PcodeOp.INT_ADD: case PcodeOp.INT_MULT: return "computed";
            default: return "op" + d.getMnemonic();
        }
    }
}
