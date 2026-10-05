// P1 Drop differential: a returned temporary is destroyed at statement scope.
struct Marker;
impl Drop for Marker { fn drop(&mut self) { println!("drop"); } }
fn main() {
    { let _temporary = Marker; }
    println!("after");
}
