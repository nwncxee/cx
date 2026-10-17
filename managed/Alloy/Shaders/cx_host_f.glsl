// CX-303: the CONTENT fragment host -- where a content pair's translated fragment stage runs, after the texel, the
// shading normal and the material terms are final and before anything lights them. ContentPrograms splices the
// pair's region in place of this stub; the stub hosts nothing, so every other program compiles the call away.
bool CxHostSurface(int feat) { return false; }
