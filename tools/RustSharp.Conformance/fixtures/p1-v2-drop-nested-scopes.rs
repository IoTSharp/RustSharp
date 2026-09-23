struct Outer; struct Inner;
impl Drop for Outer { fn drop(&mut self) { println!("outer"); } }
impl Drop for Inner { fn drop(&mut self) { println!("inner"); } }
fn main() { let _outer = Outer; { let _inner = Inner; println!("nested"); } println!("after"); }
