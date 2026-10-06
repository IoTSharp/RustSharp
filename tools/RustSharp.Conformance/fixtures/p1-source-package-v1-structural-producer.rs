pub fn pair() -> (i32, bool) { (42, true) }
pub fn sum(value: (i32, bool)) -> i32 { value.0 }
pub fn array() -> [i32; 2] { [40, 2] }
pub fn add(value: [i32; 2]) -> i32 { value[0] + value[1] }
fn main() {}
