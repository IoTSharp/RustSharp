use crate::first::*;
mod first { pub use crate::second::*; }
mod second { pub use crate::third::*; pub use self::f as g; }
mod third { pub fn f() {} }
fn main() { f(); g(); }
