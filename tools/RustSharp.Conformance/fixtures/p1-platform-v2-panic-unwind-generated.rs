// P1 platform: generated nested cleanup order at an unwind boundary.
struct Inner;
struct Outer;
impl Drop for Inner { fn drop(&mut self) { println!("inner-drop"); } }
impl Drop for Outer { fn drop(&mut self) { println!("outer-drop"); } }
fn main() {
    let _outer = Outer;
    let _inner = Inner;
    println!("body");
}
