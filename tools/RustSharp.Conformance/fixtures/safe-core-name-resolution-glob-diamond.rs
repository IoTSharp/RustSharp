mod a { pub fn f() {} }
mod b { pub use crate::a::*; }
mod c { pub use crate::a::*; }
use crate::b::*;
use crate::c::*;
use crate::b::*;
fn main() { f(); }
