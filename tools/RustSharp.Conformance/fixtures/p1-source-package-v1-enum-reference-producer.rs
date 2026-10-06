pub enum Value { Empty, Shared(&'static i32) }
pub fn none() -> Value { Value::Empty }
pub fn some() -> Value { Value::Shared(&42) }
pub fn read(value: Value) -> i32 {
    match value {
        Value::Empty => 0,
        Value::Shared(r) => *r
    }
}
fn main() {}
