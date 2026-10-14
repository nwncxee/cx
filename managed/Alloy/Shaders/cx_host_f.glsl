
// $VANILLA-HOST fs_noirwet
void CxHost_fs_noirwet() { }
// $VANILLA-END
// $VANILLA-HOST fs_noirgloss
void CxHost_fs_noirgloss() { }
// $VANILLA-END
// $VANILLA-HOST td_ground_fs
void CxHost_td_ground_fs() { }
// $VANILLA-END
// $VANILLA-HOST fs_plt_tinter
void CxHost_fs_plt_tinter() { }
// $VANILLA-END

bool CxHostSurface(int feat)
{
    if (feat == CX_FEAT_NOIRWET) CxHost_fs_noirwet();
    else if (feat == CX_FEAT_NOIRGLOSS) CxHost_fs_noirgloss();
    else if (feat == CX_FEAT_GROUNDWEATHER) CxHost_td_ground_fs();
    else if (feat == CX_FEAT_PALETTETINT) CxHost_fs_plt_tinter();
    else return false;
    return true;
}
