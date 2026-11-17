//! Gives DualLink.exe its icon and version details, as Explorer and the taskbar show them.

fn main() {
    println!("cargo:rerun-if-changed=../../assets/DualLink.ico");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }
    let mut resource = winresource::WindowsResource::new();
    resource
        .set_icon_with_id("../../assets/DualLink.ico", "1")
        .set("ProductName", "DualLink")
        .set("FileDescription", "DualLink")
        .set("LegalCopyright", "DualLink contributors");
    if let Err(error) = resource.compile() {
        // A build without the Windows SDK still works; the exe just has the default icon.
        println!("cargo:warning=The app icon could not be embedded: {error}");
    }
}
