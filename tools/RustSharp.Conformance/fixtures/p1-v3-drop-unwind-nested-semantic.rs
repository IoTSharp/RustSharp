// P1 Drop differential: nested calls unwind each live owner once.
struct Inner;
impl Drop for Inner { fn drop(&mut self) { println!("inner-drop"); } }
struct Outer;
impl Drop for Outer { fn drop(&mut self) { println!("outer-drop"); } }
fn overflow(value: i32) { println!("{}", value + 1); }
fn inner() {
    let _inner = Inner;
    println!("body");
    overflow(2147483647);
}
fn outer() {
    let _outer = Outer;
    inner();
    println!("unreachable");
}
fn main() { outer(); }
