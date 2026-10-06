use SourceProducer::{Unit, pair as source_pair, array as source_array, unit as source_unit};
pub fn make_pair() -> (i32, bool) { source_pair() }
pub fn make_array() -> [i32; 2] { source_array() }
pub fn make_unit() -> Unit { source_unit() }
pub fn read_unit(value: Unit) -> i32 { 42 }
fn main() {}
