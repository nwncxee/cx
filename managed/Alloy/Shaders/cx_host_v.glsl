// CX-303: the CONTENT vertex host -- where a content stage's translated vertex half runs, after Alloy's own vertex
// work and over its outputs. ContentPrograms splices a pair's region in place of this stub; the stub hosts nothing,
// so every other program compiles the call away.
bool CxHostVertex(CxDraw d, int id, int bb) { return false; }
