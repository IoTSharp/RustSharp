// P1 Drop differential: assignment drops the old owner before replacing it.
struct Marker;
impl Drop for Marker { fn drop(&mut self) { println!("drop"); } }
fn main() {
    let mut marker = Marker;
    marker = Marker;
    println!("body");
}
