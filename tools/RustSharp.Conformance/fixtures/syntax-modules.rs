pub mod outer { pub mod inner { pub fn value() -> i32 { 1 } } }
fn main() { outer::inner::value(); }
