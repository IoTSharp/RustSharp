pub struct Resource { pub value: i32 }

impl Drop for Resource {
    fn drop(&mut self) { println!("drop"); }
}

pub fn make() -> Resource { Resource { value: 42 } }

pub fn consume(value: Resource) { println!("{}", value.value); }

fn main() {}
