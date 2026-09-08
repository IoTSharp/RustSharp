mod a { pub use crate::b::*; }
mod b { pub use crate::a::*; pub fn f() {} }
use crate::a::*;
fn main() { f(); }
