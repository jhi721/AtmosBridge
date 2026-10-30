// Exe icon (thd-objects.rc)
fn main() {
    embed_resource::compile("thd-objects.rc", embed_resource::NONE).manifest_optional().unwrap();
}
